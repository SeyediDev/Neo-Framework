using System.Net;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.Extensions.Configuration;

static class UsageOutboxTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        DurableRetry(directory, check);
        PoisonAndScope(directory, check);
        LeaseAndBinding(directory, check);
        CorruptedEvidence(directory, check);
        RestoreDestinationAhead(directory, check);
    }

    private static void DurableRetry(string directory, Action<bool, string> check)
    {
        using var fixture = new DeliveryFixture(directory, "durable-retry"); var usage = fixture.Commit("lost-response");
        fixture.Transport.LoseNextResponse = true;
        var dispatcher = fixture.Dispatcher(); var first = Dispatch(dispatcher);
        var pending = dispatcher.ReadCursor()!;
        check(first.Status == "retry" && first.Delivered == 0 && pending.Attempt is { Failures: 1 }
            && pending.Attempt.Delivery.LedgerKey == usage.BusinessKey && fixture.Transport.Receipts.Length == 1,
            "usage destination commit with lost response persists retry before advancing local cursor");
        var sent = fixture.Transport.Deliveries.Single(); var calls = fixture.Transport.Deliveries.Count;
        using var reopened = new FabricDatabase(fixture.FabricPath);
        var restarted = fixture.Dispatcher(reopened);
        check(restarted.ReadCursor()!.Attempt == pending.Attempt && Dispatch(restarted).Status == "retry"
            && fixture.Transport.Deliveries.Count == calls,
            "durable usage retry survives Fabric restart and honors persisted backoff");
        fixture.Clock.Now += fixture.Subscription.RetryDelay + TimeSpan.FromSeconds(1);
        var retried = Dispatch(restarted); var cursor = restarted.ReadCursor()!;
        var replay = fixture.Transport.Deliveries.Last();
        check(retried.Status == "completed" && retried.Delivered == 1 && cursor.Attempt is null && cursor.LeaseOwner is null
            && cursor.LastOutboxId == fixture.Database.Outbox().Last().Id && fixture.Transport.Receipts.Length == 1,
            "lost acknowledgement retry accepts duplicate receipt and commits durable usage cursor exactly once");
        check(replay.DeliveryId == sent.DeliveryId && replay.UsageJson == sent.UsageJson && replay.EventFingerprint == sent.EventFingerprint
            && replay.SourceDatabaseId == sent.SourceDatabaseId && replay.EventId == usage.EventId,
            "usage retry preserves source delivery identity exact event bytes and fingerprint");
        calls = fixture.Transport.Deliveries.Count;
        check(Dispatch(fixture.Dispatcher(reopened)).Status == "idle" && fixture.Transport.Deliveries.Count == calls,
            "acknowledged usage cursor survives worker restart without sending again");
        Reject<InvalidOperationException>(() => fixture.Database.Transaction(() =>
            fixture.Dispatcher().DispatchBatchAsync().GetAwaiter().GetResult()), check,
            "usage dispatcher refuses ambient Fabric transaction before asynchronous delivery");
        check(fixture.Transport.Deliveries.Count == calls, "ambient transaction rejection performs no remote transport action");

        using var mismatch = new DeliveryFixture(directory, "ack-mismatch"); var mismatchUsage = mismatch.Commit("ack-mismatch");
        mismatch.Transport.AckMutation = acknowledgement => acknowledgement with { EventFingerprint = UsageEventV1.Hash("wrong-event") };
        var mismatchDispatcher = mismatch.Dispatcher(); var rejected = Dispatch(mismatchDispatcher);
        check(rejected.Status is "retry" or "blocked" && mismatchDispatcher.ReadCursor()!.Attempt is not null
            && mismatchDispatcher.ReadCursor()!.LastOutboxId < mismatch.Database.Outbox().Single(x => x.Kind == "UsageCommitted").Id,
            "mismatched usage acknowledgement cannot advance a committed destination receipt");
        mismatch.Transport.AckMutation = null; mismatch.Clock.Now += mismatch.Subscription.RetryDelay + TimeSpan.FromSeconds(1);
        var valid = Dispatch(mismatchDispatcher);
        check(valid.Status == "completed" && mismatch.Transport.Receipts.Single().BusinessKey == mismatchUsage.BusinessKey,
            "correct duplicate acknowledgement recovers previously mismatched usage response");
    }

    private static void PoisonAndScope(string directory, Action<bool, string> check)
    {
        using var fixture = new DeliveryFixture(directory, "scope");
        fixture.Database.Transaction(() =>
        {
            fixture.Database.Emit("other-kind", "OrganizationChanged", new { TenantId = fixture.Tenant }, fixture.Clock.Now);
            fixture.Database.Emit("pending-kind", "UsageAccountingPending", new { TenantId = fixture.Tenant }, fixture.Clock.Now);
            return true;
        });
        var other = fixture.Access.CreateTenant("other-usage-consumer", "Other Consumer").Id;
        fixture.Configure(other); fixture.Commit("other-consumer", other); var target = fixture.Commit("target-consumer");
        var result = Dispatch(fixture.Dispatcher());
        check(result.Status == "completed" && result.Delivered == 1 && fixture.Transport.Receipts.Single().BusinessKey == target.BusinessKey
            && fixture.Transport.Deliveries.Single().DestinationId == fixture.Subscription.DestinationId,
            "usage consumer subscription skips unrelated event kinds and other consumer events safely");
        check(fixture.Dispatcher().ReadCursor()!.LastOutboxId == fixture.Database.Outbox().Last().Id,
            "usage scoped cursor includes safely skipped rows without claiming their deliveries");

        using var poison = new DeliveryFixture(directory, "poison");
        poison.Database.Transaction(() =>
        {
            poison.Database.Emit("poisoned-usage", "UsageCommitted", new { TenantId = poison.Tenant, LedgerKey = "broken", Status = "committed", Amount = 12, Usage = new { schemaVersion = "1.0" } }, poison.Clock.Now);
            return true;
        });
        var afterPoison = poison.Commit("after-poison"); var dispatcher = poison.Dispatcher();
        var blocked = Dispatch(dispatcher); var cursor = dispatcher.ReadCursor()!;
        check(blocked.Status == "blocked" && blocked.ErrorCode is not null && poison.Transport.Deliveries.Count == 0
            && cursor.LastOutboxId < poison.Database.Outbox().Single(x => x.Key == "poisoned-usage").Id
            && !poison.Transport.Receipts.Any(x => x.BusinessKey == afterPoison.BusinessKey),
            "poisoned usage payload blocks the head without delivering later valid financial rows");
        var again = Dispatch(poison.Dispatcher());
        check(again.Status == "blocked" && poison.Transport.Deliveries.Count == 0
            && poison.Dispatcher().ReadCursor()!.LastOutboxId == cursor.LastOutboxId,
            "poisoned usage remains blocked across worker restart without silent skip");

        using var outerMismatch = new DeliveryFixture(directory, "envelope-mismatch"); var message = outerMismatch.Event("envelope-mismatch");
        using var wire = JsonDocument.Parse(message.ToWireJson());
        outerMismatch.Database.Transaction(() =>
        {
            outerMismatch.Database.Emit(message.BusinessKey + ":committed", "UsageCommitted", new
            { TenantId = outerMismatch.Tenant, LedgerKey = message.BusinessKey, Status = "committed", Amount = 99, Usage = wire.RootElement.Clone() }, outerMismatch.Clock.Now);
            return true;
        });
        check(Dispatch(outerMismatch.Dispatcher()).Status == "blocked" && outerMismatch.Transport.Deliveries.Count == 0,
            "usage dispatcher validates outer financial envelope against complete retained event");
    }

    private static void LeaseAndBinding(string directory, Action<bool, string> check)
    {
        using var fixture = new DeliveryFixture(directory, "lease-fencing"); fixture.Commit("leased-event");
        var dispatcher = fixture.Dispatcher();
        fixture.Transport.BeforeAcknowledgement = () =>
        {
            var cursor = dispatcher.ReadCursor()!;
            fixture.Database.Transaction(() =>
            {
                fixture.Database.Put("usage.outbox.cursor", UsageEventV1.Hash(fixture.Subscription.DestinationId), cursor with
                { Fence = cursor.Fence + 1, LeaseOwner = "replacement-worker", LeaseUntil = fixture.Clock.Now + fixture.Subscription.LeaseDuration });
                return true;
            });
        };
        var result = Dispatch(dispatcher); var fenced = dispatcher.ReadCursor()!;
        check(result.Status == "fenced" && fenced.Attempt is not null && fixture.Transport.Receipts.Length == 1
            && fenced.LastOutboxId < fixture.Database.Outbox().Single(x => x.Kind == "UsageCommitted").Id,
            "stale usage worker cannot acknowledge a cursor after its lease fence changes");
        var calls = fixture.Transport.Deliveries.Count;
        check(Dispatch(fixture.Dispatcher()).Status == "leased" && fixture.Transport.Deliveries.Count == calls,
            "usage worker honors another worker live lease without sending");
        fixture.Transport.BeforeAcknowledgement = null; fixture.Clock.Now += fixture.Subscription.LeaseDuration + fixture.Subscription.RetryDelay + TimeSpan.FromSeconds(1);
        var recovered = Dispatch(fixture.Dispatcher());
        check(recovered.Status == "completed" && recovered.Delivered == 1 && fixture.Transport.Receipts.Length == 1
            && fixture.Dispatcher().ReadCursor()!.Fence > fenced.Fence,
            "expired usage lease permits fenced retry of the same durable destination receipt");
        Reject<InvalidOperationException>(() => Dispatch(fixture.Dispatcher(subscription: fixture.Subscription with { EndpointBinding = "https://other.invalid/usage" })), check,
            "usage cursor cannot silently inherit delivery progress after endpoint binding changes");
        Reject<InvalidOperationException>(() => Dispatch(fixture.Dispatcher(subscription: fixture.Subscription with { ConsumerTenantId = Guid.NewGuid() })), check,
            "usage cursor cannot inherit delivery progress for a different consumer scope");
        var secondDestination = fixture.Subscription with { DestinationId = "independent-destination", EndpointBinding = "https://independent.invalid/usage" };
        check(Dispatch(fixture.Dispatcher(subscription: secondDestination)).Delivered == 1
            && fixture.Database.All<UsageOutboxCursor>("usage.outbox.cursor").Length == 2,
            "independent usage destination gets a separate durable delivery cursor");
    }

    private static void RestoreDestinationAhead(string directory, Action<bool, string> check)
    {
        using var fixture = new DeliveryFixture(directory, "restore-ahead"); fixture.Commit("restored-event");
        var dispatcher = fixture.Dispatcher(); Dispatch(dispatcher, 1);
        var old = dispatcher.ReadCursor()!; var bundle = Path.Combine(fixture.Root, "bundle");
        FabricRecovery.Backup(fixture.FabricPath, fixture.RegistryPath, bundle, true);
        check(Dispatch(dispatcher).Delivered == 1 && fixture.Transport.Receipts.Length == 1,
            "usage destination advances after an actual source backup");
        var delivery = fixture.Transport.Deliveries.Last(); var destinationAhead = fixture.Transport.Receipts.Single();
        var restored = Path.Combine(fixture.Root, "restored"); FabricRecovery.Restore(bundle, restored, true);
        using var database = new FabricDatabase(Path.Combine(restored, "fabric.db"));
        var replay = fixture.Dispatcher(database); var restoredCursor = replay.ReadCursor()!;
        check(restoredCursor.LastOutboxId == old.LastOutboxId && restoredCursor.SourceDatabaseId == old.SourceDatabaseId,
            "actual restore preserves source stream identity and rolls back only local usage delivery progress");
        var result = Dispatch(replay);
        check(result.Status == "completed" && result.Delivered == 1 && fixture.Transport.Receipts.Single() == destinationAhead
            && fixture.Transport.Deliveries.Last().DeliveryId == delivery.DeliveryId
            && fixture.Transport.Deliveries.Last().UsageJson == delivery.UsageJson,
            "restored source safely resends stable usage to an ahead durable destination without duplicate effect");
        check(Dispatch(replay).Status == "idle" && replay.ReadCursor()!.Attempt is null,
            "restored usage dispatcher durably acknowledges the duplicate receipt");
    }

    private static void CorruptedEvidence(string directory, Action<bool, string> check)
    {
        using var anchor = new DeliveryFixture(directory, "anchor-corruption"); anchor.Commit("anchor-event");
        var dispatcher = anchor.Dispatcher(); Dispatch(dispatcher); var cursor = dispatcher.ReadCursor()!;
        anchor.Database.Transaction(() =>
        {
            anchor.Database.Put(UsageOutboxDispatcher.CursorKind, UsageEventV1.Hash(anchor.Subscription.DestinationId),
                cursor with { LastOutboxPayloadHash = UsageEventV1.Hash("different-restored-outbox") }); return true;
        });
        var calls = anchor.Transport.Deliveries.Count;
        Reject<InvalidDataException>(() => Dispatch(anchor.Dispatcher()), check,
            "usage cursor cannot advance against a changed restore anchor payload");
        check(anchor.Transport.Deliveries.Count == calls, "usage restore anchor validation performs no remote resend");

        using var stream = new DeliveryFixture(directory, "stream-corruption"); stream.Commit("stream-event"); Dispatch(stream.Dispatcher());
        stream.Database.Transaction(() =>
        {
            stream.Database.Put(UsageOutboxDispatcher.DatabaseIdentityKind, UsageOutboxDispatcher.DatabaseIdentityKey,
                new { Id = Guid.NewGuid().ToString("D") }); return true;
        });
        Reject<InvalidOperationException>(() => Dispatch(stream.Dispatcher()), check,
            "usage cursor cannot inherit progress from another durable source database identity");

        using var retained = new DeliveryFixture(directory, "attempt-corruption"); retained.Commit("retained-attempt");
        retained.Transport.LoseNextResponse = true; var retainedDispatcher = retained.Dispatcher(); Dispatch(retainedDispatcher);
        var pending = retainedDispatcher.ReadCursor()!;
        retained.Database.Transaction(() =>
        {
            retained.Database.Put(UsageOutboxDispatcher.CursorKind, UsageEventV1.Hash(retained.Subscription.DestinationId),
                pending with { Attempt = pending.Attempt! with { PayloadHash = UsageEventV1.Hash("changed-attempt") } }); return true;
        });
        calls = retained.Transport.Deliveries.Count;
        check(Dispatch(retained.Dispatcher()).Status == "blocked" && retained.Transport.Deliveries.Count == calls
            && retained.Dispatcher().ReadCursor()!.LastOutboxId == pending.LastOutboxId,
            "changed retained retry evidence blocks delivery without replacing the stable pending attempt");

        using var ledger = new DeliveryFixture(directory, "ledger-corruption"); var usage = ledger.Commit("ledger-event");
        var account = ledger.Accounting.Read(ledger.Tenant).Account;
        ledger.Database.Transaction(() =>
        {
            ledger.Database.Put("accounting", ledger.Tenant.ToString(), account with
            { Entries = account.Entries.Select(x => x.Kind == "usage" && x.Key == usage.BusinessKey ? x with { Amount = 99 } : x).ToArray() });
            return true;
        });
        check(Dispatch(ledger.Dispatcher()).Status == "blocked" && ledger.Transport.Deliveries.Count == 0,
            "usage dispatcher will not publish committed envelope whose durable financial charge no longer matches");
    }

    private static UsageDispatchResult Dispatch(UsageOutboxDispatcher dispatcher, int take = 100) => dispatcher.DispatchBatchAsync(take).GetAwaiter().GetResult();
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }

    private sealed class DeliveryFixture : IDisposable
    {
        public string Root { get; }
        public string FabricPath { get; }
        public string RegistryPath { get; }
        public FabricDatabase Database { get; }
        public PersistentAccessManagement Access { get; }
        public Guid Owner { get; }
        public Guid Tenant { get; }
        public DeliveryClock Clock { get; } = new(new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
        public AccountingStore Accounting { get; }
        public MeasuredUsageStore Intake { get; }
        public DurableDestination Transport { get; }
        public UsageOutboxSubscription Subscription { get; }
        public DeliveryFixture(string directory, string name)
        {
            Root = Path.Combine(directory, "outbox-" + name); Directory.CreateDirectory(Root);
            FabricPath = Path.Combine(Root, "fabric.db"); RegistryPath = Path.Combine(Root, "registry.db");
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = RegistryPath }).Build();
            var registry = new PlatformRegistry(settings); Database = new FabricDatabase(FabricPath);
            Access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(registry)); Owner = Access.GetTenants().Single().Id;
            Tenant = Access.CreateTenant("outbox-consumer", "Outbox Consumer").Id;
            registry.SetTenantSource(Access.GetTenants); registry.SetMembershipSource(Access.GetUsers);
            registry.Register(new(Guid.NewGuid(), "organization-fabric", "Organization", "organization", "fanasa.ayin", null, null, Owner, "fanasa"), "test");
            Accounting = new AccountingStore(Root, Access, Clock, Database); Configure(Tenant);
            Intake = new MeasuredUsageStore(Database, Accounting, new AllowSnapshot(), Clock);
            Transport = new DurableDestination(new FabricDatabase(Path.Combine(Root, "destination.db")));
            Subscription = new("synthetic-usage-destination", "https://usage.invalid/receive", Tenant, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        }
        public void Configure(Guid tenant) => Accounting.Execute(tenant, new(0, "configure", "configure", "postpaid", "IRR", 1000,
            null, null, 0, 0, 0, "synthetic", null, null), "test");
        public UsageOutboxDispatcher Dispatcher(FabricDatabase? database = null, UsageOutboxSubscription? subscription = null)
            => new(database ?? Database, Transport, subscription ?? Subscription, Clock);
        public UsageEventV1 Commit(string operation, Guid? consumer = null)
        { var usage = Event(operation, consumer); Intake.Accept(usage); return usage; }
        public UsageEventV1 Event(string operation, Guid? consumer = null) => new("1.0", Guid.NewGuid(), Clock.Now.AddMinutes(-1), Clock.Now,
            new("test-backend", "product-backend", "test-host"), "development", Owner, consumer ?? Tenant, "organization-fabric", "test-contract", 7,
            "test-allocation", "2026-10", new(operation, "organization.unit.save/v1", "test-member", Guid.NewGuid(), UsageEventV1.Hash("request:" + operation),
                Clock.Now.AddMinutes(-2), "organization-unit-committed/v1"), new("decision:" + operation, "organization-permission/v1", "credit:" + operation),
            new("organization.change", "1", "change", "1"), new("test-rate", 7, Clock.Now.AddDays(-1), Clock.Now.AddDays(1), "12", "IRR", 0, "away-from-zero", "postpaid"),
            "completed", new("receipt:" + operation, UsageEventV1.Hash("result:" + operation), "fabric://test/" + operation), Guid.NewGuid());
        public void Dispose() { Transport.Dispose(); Database.Dispose(); }
    }
    private sealed record DestinationReceipt(string BusinessKey, string OperationFingerprint, string UsageJson);
    private sealed class DurableDestination(FabricDatabase database) : IUsageOutboxTransport, IDisposable
    {
        public List<UsageOutboxDelivery> Deliveries { get; } = [];
        public bool LoseNextResponse { get; set; }
        public Func<UsageOutboxAcknowledgement, UsageOutboxAcknowledgement>? AckMutation { get; set; }
        public Action? BeforeAcknowledgement { get; set; }
        public DestinationReceipt[] Receipts => database.All<DestinationReceipt>("synthetic.usage.receipt");
        public Task<UsageOutboxAcknowledgement> SendAsync(UsageOutboxDelivery delivery, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Deliveries.Add(delivery);
            var message = UsageEventV1.FromJson(delivery.UsageJson);
            if (message.EventId != delivery.EventId || message.EventFingerprint != delivery.EventFingerprint || message.BusinessKey != delivery.LedgerKey)
                throw new InvalidDataException("Synthetic destination received a mismatched delivery.");
            var duplicate = database.Transaction(() =>
            {
                var previous = database.Read<DestinationReceipt>("synthetic.usage.receipt", message.BusinessKey);
                if (previous is not null)
                {
                    if (previous.OperationFingerprint != message.OperationFingerprint) throw new InvalidOperationException("Destination business identity conflict.");
                    return true;
                }
                database.Put("synthetic.usage.receipt", message.BusinessKey, new DestinationReceipt(message.BusinessKey, message.OperationFingerprint, delivery.UsageJson));
                return false;
            });
            if (LoseNextResponse) { LoseNextResponse = false; throw new HttpRequestException("Destination committed before synthetic response loss."); }
            BeforeAcknowledgement?.Invoke();
            var acknowledgement = new UsageOutboxAcknowledgement(delivery.DestinationId, delivery.LedgerKey, delivery.EventId, delivery.EventFingerprint,
                duplicate ? "duplicate" : "accepted");
            return Task.FromResult(AckMutation?.Invoke(acknowledgement) ?? acknowledgement);
        }
        public void Dispose() => database.Dispose();
    }
    private sealed class AllowSnapshot : IUsageSnapshotValidator { public void ValidateNew(UsageEventV1 message) { } }
    private sealed class DeliveryClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
