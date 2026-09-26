using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Neo.Application.Exceptions;
using Neo.Application.Features.Crud;
using Neo.Domain.Entities.Base;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Concurrency;
using Neo.Infrastructure.Features.Crud;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class CrudResourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Query_pages_in_SQL_and_rejects_undeclared_filters()
    {
        await using var fixture = await Fixture.Create();
        await using var db = fixture.Context();
        for (var i = 0; i < 25; i++) db.Add(new Entity { Id=Guid.NewGuid(), Name=$"item-{i:00}" });
        await db.SaveChangesAsync(Ct); db.ChangeTracker.Clear();
        var service = Service(db);
        var rows = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await service.ListAsync(new() { PageNumber=page,PageSize=10,Sort="name" }, Ct);
            rows.AddRange(result.Items.Select(x => x.Data.Name)); Assert.Equal(page<3,result.HasNext);
            Assert.Contains("LIMIT",fixture.Commands.LastRead);
        }
        Assert.Equal(Enumerable.Range(0,25).Select(i=>$"item-{i:00}"),rows);
        await Assert.ThrowsAsync<BadRequestException>(()=>service.ListAsync(new() {Filters = new() { ["ServerValue"]="other" }},Ct));
        await Assert.ThrowsAsync<BadRequestException>(()=>service.ListAsync(new() {Sort="ServerValue"},Ct));
    }
    [Fact]
    public async Task Entity_translation_and_outbox_commit_or_rollback_together()
    {
        await using var fixture = await Fixture.Create();
        await using var db = fixture.Context();
        var service = Service(db,new Participant());
        var created = await service.CreateAsync(new("good"),Ct);
        Assert.NotEqual(Guid.Empty,created.Version);
        Assert.Equal(1,await db.Set<Translation>().CountAsync(Ct));
        Assert.Equal(1,await db.Set<OutboxMessage>().CountAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.CreateAsync(new("fail"),Ct));
        Assert.Equal(1,await db.Set<Entity>().CountAsync(Ct));
        Assert.Equal(1,await db.Set<Translation>().CountAsync(Ct));
        Assert.Equal(1,await db.Set<OutboxMessage>().CountAsync(Ct));
        Assert.Empty(db.ChangeTracker.Entries());
    }
    [Fact]
    public async Task Stale_update_and_delete_cannot_overwrite_new_version()
    {
        await using var fixture = await Fixture.Create();
        await using var db = fixture.Context(); var service=Service(db);
        var created=await service.CreateAsync(new("first"),Ct);
        var changed=await service.UpdateAsync(created.Id,new("second"),created.Version,Ct);
        Assert.NotEqual(created.Version,changed.Version);
        var error=await Assert.ThrowsAsync<EntityConcurrencyException>(()=>service.UpdateAsync(created.Id,new("lost"),created.Version,Ct));
        Assert.Equal("stale_version",error.Code);
        await Assert.ThrowsAsync<EntityConcurrencyException>(()=>service.DeleteAsync(created.Id,created.Version,Ct));
        Assert.Equal("second",(await service.GetAsync(created.Id,Ct))!.Data.Name);
        await service.DeleteAsync(created.Id,changed.Version,Ct);
        Assert.Null(await service.GetAsync(created.Id,Ct));
    }
    [Fact]
    public async Task EF_token_also_protects_non_CRUD_writes_in_two_contexts()
    {
        await using var fixture=await Fixture.Create();
        await using var first=fixture.Context(); await using var second=fixture.Context();
        var created=await Service(first).CreateAsync(new("original"),Ct);
        var a=await first.Set<Entity>().SingleAsync(Ct); var b=await second.Set<Entity>().SingleAsync(Ct);
        a.Name="winner"; b.Name="loser";
        await first.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>second.SaveChangesAsync(Ct));
        Assert.NotEqual(created.Version,a.Version);
        await using var check=fixture.Context(); Assert.Equal("winner",(await check.Set<Entity>().SingleAsync(Ct)).Name);
    }
    [Fact]
    public async Task Pessimistic_mode_fails_explicitly_on_SQLite()
    {
        await using var fixture=await Fixture.Create(); await using var db=fixture.Context();
        var service = new EfCrudService<Context,Definition,CreateInput,UpdateInput,ReadDto,Entity,Guid>(db,new Definition { Mode=EntityConcurrencyMode.Pessimistic },[]);
        Assert.Contains(service.ValidateConfiguration(),x=>x.Contains("SQL Server"));
        await using var tx=await db.Database.BeginTransactionAsync(Ct);
        await Assert.ThrowsAsync<NotSupportedException>(()=>SqlServerRowLock.AcquireAsync<Entity>(db,Guid.NewGuid(),TimeSpan.FromSeconds(1),Ct));
    }
    [Fact]
    public async Task Scope_protects_reads_and_mutations_and_mapping_keeps_server_fields()
    {
        await using var fixture=await Fixture.Create(); await using var db=fixture.Context();
        var hidden=new Entity { Id=Guid.NewGuid(),Name="other",Owner="other-tenant" }; db.Add(hidden); await db.SaveChangesAsync(Ct); db.ChangeTracker.Clear();
        var service=Service(db);
        Assert.Null(await service.GetAsync(hidden.Id,Ct));
        await Assert.ThrowsAsync<Ardalis.GuardClauses.NotFoundException>(()=>service.UpdateAsync(hidden.Id,new("bad"),hidden.Version,Ct));
        var created=await service.CreateAsync(new("mine"),Ct);
        await service.UpdateAsync(created.Id,new("new"),created.Version,Ct);
        Assert.Equal("server-only",(await db.Set<Entity>().AsNoTracking().SingleAsync(x=>x.Id==created.Id,Ct)).ServerValue);
    }
    internal static EfCrudService<Context,Definition,CreateInput,UpdateInput,ReadDto,Entity,Guid> Service(Context db,params ICrudTransactionParticipant<Context,Entity>[] participants) => new(db,new Definition(),participants);
    public sealed record CreateInput([property:Required] string Name);
    public sealed record UpdateInput([property:Required] string Name);
    public sealed record ReadDto(string Name);
    public sealed class Entity : BaseEntity<Guid>,IConcurrencyVersion
    { public string Name {get;set;}=""; public string Owner {get;set;}="mine"; public string ServerValue {get;set;}="server-only"; public Guid Version {get;set;} }
    public sealed class Translation { public int Id {get;set;} public Guid EntityId {get;set;} public string Text {get;set;}=""; }
    public sealed class Definition : CrudDefinition<CreateInput,UpdateInput,ReadDto,Entity,Guid>
    {
        public override string Name=>"test-products";
        public EntityConcurrencyMode Mode {get;init;}
        public TimeSpan Wait {get;init;} = TimeSpan.FromSeconds(3);
        public override TimeSpan LockWait => Wait;
        public override EntityConcurrencyMode Concurrency=>Mode;
        public override IReadOnlyDictionary<CrudOperation,string?> Policies=>new Dictionary<CrudOperation,string?> { [CrudOperation.List]=null,[CrudOperation.Read]=null,[CrudOperation.Create]=null,[CrudOperation.Update]=null,[CrudOperation.Delete]=null };
        public override IReadOnlyDictionary<string,Func<IQueryable<Entity>,bool,IOrderedQueryable<Entity>>> Sorts=>new Dictionary<string,Func<IQueryable<Entity>,bool,IOrderedQueryable<Entity>>> { ["name"]=(q,descending)=>descending?q.OrderByDescending(x=>x.Name):q.OrderBy(x=>x.Name) };
        public override IQueryable<Entity> Scope(IQueryable<Entity> query)=>query.Where(x=>x.Owner=="mine");
        public override Entity Create(CreateInput input)=>new() {Id=Guid.NewGuid(),Name=input.Name};
        public override void Update(Entity entity,UpdateInput input)=>entity.Name=input.Name;
        public override ReadDto Read(Entity entity)=>new(entity.Name);
    }
    public sealed class Participant : ICrudTransactionParticipant<Context,Entity>
    {
        public async Task StageAsync(Context db,CrudOperation operation,Entity entity,object? input,CancellationToken ct)
        {
            db.Add(new Translation {EntityId=entity.Id,Text=entity.Name});
            db.Add(new OutboxMessage {MessageName="changed",MessageType="test",MessageContent="{}"});
            await db.SaveChangesAsync(ct);
            if (entity.Name=="fail") throw new InvalidOperationException("translation unavailable");
        }
    }
    public sealed class Context(DbContextOptions<Context> options):DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Entity>(); model.Entity<Translation>();
            model.Entity<OutboxMessage>().Ignore(x=>x.CreatedById).Ignore(x=>x.LastModifiedById);
            model.ConfigureNeoConcurrency();
        }
    }
    private sealed class Capture:DbCommandInterceptor
    {
        public string LastRead {get;private set;}="";
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        { LastRead=command.CommandText;return ValueTask.FromResult(result); }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly SqliteConnection anchor=new($"Data Source=crud-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        public Capture Commands {get;}=new();
        public Context Context()=>new(new DbContextOptionsBuilder<Context>().UseSqlite(anchor.ConnectionString).AddInterceptors(new NeoConcurrencyInterceptor(),Commands).Options);
        public static async Task<Fixture> Create() {var f=new Fixture();await f.anchor.OpenAsync(Ct);await using var db=f.Context();await db.Database.EnsureCreatedAsync(Ct);return f;}
        public async ValueTask DisposeAsync()=>await anchor.DisposeAsync();
    }
}
