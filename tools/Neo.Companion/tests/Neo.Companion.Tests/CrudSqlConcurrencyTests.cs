using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.Application.Features.Crud;
using Neo.Domain.Features.Concurrency;
using Neo.Infrastructure.Features.Crud;
using Xunit;
using Context = Neo.Companion.Tests.CrudResourceTests.Context;
using Entity = Neo.Companion.Tests.CrudResourceTests.Entity;
using Definition = Neo.Companion.Tests.CrudResourceTests.Definition;
using CreateInput = Neo.Companion.Tests.CrudResourceTests.CreateInput;
using UpdateInput = Neo.Companion.Tests.CrudResourceTests.UpdateInput;
using ReadDto = Neo.Companion.Tests.CrudResourceTests.ReadDto;

namespace Neo.Companion.Tests;

public sealed class CrudSqlConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Pessimistic_scope_read_is_bounded_when_another_writer_holds_an_exclusive_lock()
    {
        await using var f = await Fixture.Create();
        await using var owner = f.Context(); await using var contender = f.Context();
        var original = await Service(owner, EntityConcurrencyMode.Optimistic).CreateAsync(new("original"), Ct);
        await using var tx = await owner.Database.BeginTransactionAsync(Ct);
        var row = await owner.Set<Entity>().SingleAsync(Ct);
        row.Name = "uncommitted"; await owner.SaveChangesAsync(Ct);
        var service = new EfCrudService<Context,Definition,CreateInput,UpdateInput,ReadDto,Entity,Guid>(
            contender, new Definition { Mode = EntityConcurrencyMode.Pessimistic, Wait = TimeSpan.FromSeconds(1) }, []);
        var timeout = contender.Database.GetCommandTimeout();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<EntityConcurrencyException>(() => service.UpdateAsync(original.Id, new("next"), original.Version, Ct));
        Assert.Equal("resource_busy", error.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The scope pre-read must honor LockWait too.");
        Assert.Equal(timeout, contender.Database.GetCommandTimeout());
        await tx.RollbackAsync(Ct);
        Assert.Equal("next", (await service.UpdateAsync(original.Id, new("next"), original.Version, Ct)).Data.Name);
    }
    [Theory]
    [InlineData(EntityConcurrencyMode.Optimistic)]
    [InlineData(EntityConcurrencyMode.Pessimistic)]
    public async Task Two_simultaneous_writers_produce_one_success_and_one_conflict(EntityConcurrencyMode mode)
    {
        await using var f=await Fixture.Create();
        await using var first=f.Context(); await using var second=f.Context();
        var original=await Service(first,mode).CreateAsync(new("original"),Ct);
        var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Change(Context db,string name)
        {
            await start.Task;
            try { await Service(db,mode).UpdateAsync(original.Id,new(name),original.Version,Ct); return true; }
            catch (EntityConcurrencyException ex) { Assert.Equal("stale_version",ex.Code); return false; }
        }
        var a=Change(first,"first"); var b=Change(second,"second"); start.SetResult();
        var results=await Task.WhenAll(a,b); Assert.Equal(1,results.Count(x=>x));
        await using var check=f.Context(); var row=await check.Set<Entity>().SingleAsync(Ct);
        Assert.Equal(results[0]?"first":"second",row.Name);
        Assert.NotEqual(original.Version,row.Version);
    }

    [Fact]
    public async Task SQL_lock_is_exclusive_bounded_cancellable_and_released_on_rollback()
    {
        await using var f=await Fixture.Create();
        await using var owner=f.Context(); await using var contender=f.Context();
        var original=await Service(owner,EntityConcurrencyMode.Optimistic).CreateAsync(new("record"),Ct);
        await using var tx=await owner.Database.BeginTransactionAsync(Ct);
        await SqlServerRowLock.AcquireAsync<Entity>(owner,original.Id,TimeSpan.FromSeconds(3),Ct);
        await using (var second=await contender.Database.BeginTransactionAsync(Ct))
        {
            var error=await Assert.ThrowsAsync<EntityConcurrencyException>(()=>SqlServerRowLock.AcquireAsync<Entity>(contender,original.Id,TimeSpan.FromSeconds(1),Ct));
            Assert.Equal("resource_busy",error.Code);
            await second.RollbackAsync(Ct);
        }
        await using (var second=await contender.Database.BeginTransactionAsync(Ct))
        {
            using var canceled=CancellationTokenSource.CreateLinkedTokenSource(Ct); canceled.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>SqlServerRowLock.AcquireAsync<Entity>(contender,original.Id,TimeSpan.FromSeconds(5),canceled.Token));
            await second.RollbackAsync(Ct);
        }
        await tx.RollbackAsync(Ct);
        await using var retry=await contender.Database.BeginTransactionAsync(Ct);
        await SqlServerRowLock.AcquireAsync<Entity>(contender,original.Id,TimeSpan.FromSeconds(3),Ct);
        await retry.CommitAsync(Ct);
    }
    private static EfCrudService<Context,Definition,CreateInput,UpdateInput,ReadDto,Entity,Guid> Service(Context db,EntityConcurrencyMode mode)=>new(db,new Definition {Mode=mode},[]);
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly string connection;
        private bool created;
        private Fixture(string configured)
        {
            var builder=new SqlConnectionStringBuilder(configured) {InitialCatalog="NeoCrudTest_"+Guid.NewGuid().ToString("N")};
            connection=builder.ConnectionString;
        }
        public Context Context()=>new(new DbContextOptionsBuilder<Context>().UseSqlServer(connection).AddInterceptors(new NeoConcurrencyInterceptor()).Options);
        public static async Task<Fixture> Create()
        {
            var configured=Environment.GetEnvironmentVariable("NEO_CRUD_SQL");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(configured),"Set NEO_CRUD_SQL to an isolated SQL Server; a unique NeoCrudTest_* database is created and removed.");
            var f=new Fixture(configured!); await using var db=f.Context();
            f.created=await db.Database.EnsureCreatedAsync(Ct); return f;
        }
        public async ValueTask DisposeAsync()
        {
            if (created && new SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("NeoCrudTest_",StringComparison.Ordinal))
            { await using var db=Context(); await db.Database.EnsureDeletedAsync(CancellationToken.None); }
        }
    }
}
