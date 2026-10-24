using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

static class PipSchedulingTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "pip-scheduling"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fabric.db");
        using var database = new FabricDatabase(path);
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-scheduling", "PIP scheduling").Id;
        var store = new PipStore(database, access);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var source = store.Register(tenant, "Scheduled directory", "https://hr.example.test/attributes", "hr.example.test", null,
            enabled: true, syncSubjects: ["alice"], pollIntervalSeconds: 30);
        var educational = store.Register(tenant, "Education", "https://example.invalid/attributes", "example.invalid", null,
            enabled: true, syncSubjects: ["example"], pollIntervalSeconds: 30, educational: true);
        var disabled = store.Register(tenant, "Disabled directory", "https://disabled.example.test/attributes", "disabled.example.test", null,
            syncSubjects: ["disabled"], pollIntervalSeconds: 30);
        check(store.ScheduledSources().Select(x => x.Id).SequenceEqual([source.Id]),
            "only enabled non-educational sources enter the PIP scheduler");
        var connector = new ScheduledConnector(clock);
        var sync = new PipSyncService(store, connector, clock);
        var worker = new PipSyncWorker(store, sync, clock, NullLogger<PipSyncWorker>.Instance);
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1 && connector.Calls == 1
            && store.Checkpoint(tenant, source.Id, "alice")?.Status == "succeeded",
            "scheduled PIP synchronization persists a successful subject checkpoint");
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 0 && connector.Calls == 1,
            "subject checkpoint prevents repeated polling before its configured interval");
        clock.At = clock.At.AddSeconds(31);
        connector.Fail = true;
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1 && connector.Calls == 2
            && store.Checkpoint(tenant, source.Id, "alice")?.Status == "failed",
            "failed scheduled PIP synchronization advances the durable retry checkpoint");
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 0 && connector.Calls == 2,
            "failed PIP sources wait their polling interval rather than retrying every scan");
        clock.At = clock.At.AddSeconds(31); connector.Fail = false;
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1 && connector.Calls == 3
            && store.Checkpoint(tenant, source.Id, "alice")?.Status == "succeeded",
            "scheduled PIP synchronization recovers after a transient upstream failure");
        check(store.Checkpoint(tenant, educational.Id, "example") is null && store.Checkpoint(tenant, disabled.Id, "disabled") is null,
            "educational and disabled sources never receive background HTTP synchronization");
        var latest = store.Checkpoint(tenant, source.Id, "alice")!;
        store.RecordRun(latest with { Id = Guid.NewGuid(), StartedAt = clock.At.AddSeconds(-1), CompletedAt = clock.At.AddSeconds(-1) });
        check(store.Checkpoint(tenant, source.Id, "alice")?.Id == latest.Id,
            "late older completion cannot move a durable PIP polling checkpoint backwards");

        using var secondDatabase = new FabricDatabase(path);
        var secondStore = new PipStore(secondDatabase, new PersistentAccessManagement(secondDatabase, new InMemoryAccessManagement()));
        var firstRun = Guid.NewGuid(); var secondRun = Guid.NewGuid();
        check(store.TryLease(tenant, source.Id, "leased", firstRun, clock.At)
            && !secondStore.TryLease(tenant, source.Id, "leased", secondRun, clock.At),
            "PIP synchronization lease excludes a second database instance for the same subject");
        secondStore.ReleaseLease(tenant, source.Id, "leased", secondRun);
        check(!secondStore.TryLease(tenant, source.Id, "leased", secondRun, clock.At),
            "a different run cannot release the active PIP subject lease");
        check(!secondStore.TryLease(tenant, source.Id, "leased", secondRun, clock.At.AddSeconds(source.TimeoutSeconds + 29))
            && secondStore.TryLease(tenant, source.Id, "leased", secondRun, clock.At.AddSeconds(source.TimeoutSeconds + 31)),
            "abandoned PIP leases expire after the source timeout plus completion grace period");
        store.ReleaseLease(tenant, source.Id, "leased", firstRun);
        check(!store.TryLease(tenant, source.Id, "leased", Guid.NewGuid(), clock.At.AddSeconds(source.TimeoutSeconds + 31)),
            "late completion cannot release a replacement PIP lease");
        secondStore.ReleaseLease(tenant, source.Id, "leased", secondRun);

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancellationObserved = false;
        try { new PipSyncService(store, new CancelledConnector(), clock).SyncAsync(tenant, source.Id, "cancelled", cancelled.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        var recoveryRun = Guid.NewGuid();
        check(cancellationObserved && store.Checkpoint(tenant, source.Id, "cancelled")?.Status == "cancelled"
            && store.TryLease(tenant, source.Id, "cancelled", recoveryRun, clock.At),
            "caller cancellation is recorded and releases the PIP subject lease");
        store.ReleaseLease(tenant, source.Id, "cancelled", recoveryRun);
        var timeoutRun = new PipSyncService(store, new TimeoutConnector(), clock).SyncAsync(tenant, source.Id, "timed-out").GetAwaiter().GetResult();
        check(timeoutRun.Status == "failed" && timeoutRun.Error == "timeout", "upstream timeout is distinct from caller cancellation");
        var timeoutRecovery = Guid.NewGuid();
        check(store.TryLease(tenant, source.Id, "timed-out", timeoutRecovery, clock.At), "upstream timeout releases the PIP subject lease");
        store.ReleaseLease(tenant, source.Id, "timed-out", timeoutRecovery);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed class ScheduledConnector(MutableClock clock) : IPipExternalConnector
    {
        public int Calls;
        public bool Fail { get; set; }
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref Calls);
            if (Fail) return Task.FromException<PipFetchedSnapshot>(new HttpRequestException("upstream-unavailable"));
            var attributes = new Dictionary<string, string> { ["employment.active"] = "true" };
            var version = "scheduled-" + attempt;
            var at = clock.GetUtcNow();
            return Task.FromResult(new PipFetchedSnapshot(source.TenantId, subject, source.SchemaVersion, attributes, version, null, at,
                PipStore.HashAttributes(source.TenantId, subject, source.SchemaVersion, attributes, version, at)));
        }
    }

    private sealed class CancelledConnector : IPipExternalConnector
    {
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
            => Task.FromCanceled<PipFetchedSnapshot>(cancellationToken);
    }

    private sealed class TimeoutConnector : IPipExternalConnector
    {
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
            => Task.FromException<PipFetchedSnapshot>(new TaskCanceledException("synthetic-upstream-timeout"));
    }
}
