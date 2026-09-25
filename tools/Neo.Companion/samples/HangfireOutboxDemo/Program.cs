using Hangfire;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Dto;
using Neo.Application.Features.Outbox.Implementation;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Telementry;
using Neo.Infrastructure.Features.Outbox;
using Neo.Infrastructure.Features.Queue.Hangfire;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5091");
var connection = builder.Configuration.GetConnectionString("Demo") ?? throw new InvalidOperationException("Demo connection required.");
if (!new SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("NeoHangfireDemo", StringComparison.Ordinal))
    throw new InvalidOperationException("This teaching app may initialize only a NeoHangfireDemo database.");
builder.Services.AddDbContext<DemoContext>(o => o.UseSqlServer(connection));
builder.Services.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<WriteReceiptHandler>());
builder.Services.Configure<TelemetryOptions>(o => o.ApplicationName = "Neo.HangfireOutboxDemo");
builder.Services.AddScoped<IRequesterUser, DemoRequester>();
builder.Services.AddScoped<ITelementryObject, TelementryObject>();
builder.Services.AddScoped<ITelementryBehaviour, TelementryBehaviour>();
builder.Services.AddNeoEfOutbox<DemoContext>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IDistributedLock, MemoryDistributedLock>(); // EF claims coordinate workers across processes here.
builder.Services.AddScoped<IOutboxJobScheduler, DefaultOutboxJobScheduler>();
builder.Services.AddScoped<IProcessOutboxRecurringJob, ProcessOutboxRecurringJob>();
builder.Configuration["Hangfire:Storage"] = "SqlServer";
builder.Configuration["Hangfire:ConnectionString"] = connection;
builder.Configuration["Hangfire:WorkerCount"] = "2";
builder.Services.AddNeoHangfire(builder.Configuration);
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DemoContext>().Database.EnsureCreatedAsync();
    _ = scope.ServiceProvider.GetRequiredService<IProcessOutboxRecurringJob>();
}
app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<IProcessOutboxRecurringJob>(
    "neo-outbox", "outbox", x => x.Run(), "* * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapPost("/receipts", async (WriteReceipt command, bool? rollback, DemoContext db, CancellationToken ct) =>
{
    if (command.OperationId == Guid.Empty) return Results.BadRequest();
    // Business state and Requested row share a transaction. No enqueue within this transaction.
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var row = new OutboxMessage { MessageName = nameof(WriteReceipt), MessageType = typeof(WriteReceipt).FullName!,
        MessageContent = System.Text.Json.JsonSerializer.Serialize(command) };
    db.Requests.Add(new ReceiptRequest { Id = command.OperationId }); db.Add(row);
    await db.SaveChangesAsync(ct);
    if (rollback == true) await tx.RollbackAsync(ct); else await tx.CommitAsync(ct);
    return Results.Ok(new { outboxId = row.Id, rolledBack = rollback == true });
});
app.MapPost("/dispatch", async (IProcessOutboxRecurringJob worker) => { await worker.Run(); return Results.Accepted(); });
app.MapGet("/outbox/{id:long}", async (long id, DemoContext db, CancellationToken ct) =>
    await db.Set<OutboxMessage>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct));
app.MapGet("/receipts/{id:guid}", async (Guid id, DemoContext db, CancellationToken ct) => new
{
    requests = await db.Requests.CountAsync(x => x.Id == id, ct),
    effects = await db.Receipts.CountAsync(x => x.Id == id, ct)
});
await app.RunAsync();

public sealed record WriteReceipt(Guid OperationId, bool Fail = false) : IRequest, IOutboxMessage;
public sealed class ReceiptRequest { public Guid Id { get; set; } }
public sealed class Receipt { public Guid Id { get; set; } }
public sealed class WriteReceiptHandler(DemoContext db) : IRequestHandler<WriteReceipt>
{
    public async Task Handle(WriteReceipt request, CancellationToken ct)
    {
        if (!await db.Receipts.AnyAsync(x => x.Id == request.OperationId, ct)) db.Receipts.Add(new Receipt { Id = request.OperationId });
        await db.SaveChangesAsync(ct); // Inside the worker transaction; rollback includes this save.
        if (request.Fail) throw new InvalidOperationException("Demonstrated failure after saving: the worker rolls back this effect.");
    }
}
public sealed class DemoContext(DbContextOptions<DemoContext> options) : DbContext(options)
{
    public DbSet<ReceiptRequest> Requests => Set<ReceiptRequest>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<OutboxMessage>().Ignore(x => x.CreatedById).Ignore(x => x.LastModifiedById);
        model.Entity<OutboxMessage>().HasIndex(x => new { x.OutboxState, x.NextAttemptAtUtc, x.DeliveryLeaseUntilUtc });
    }
}
