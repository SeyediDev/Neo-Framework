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
        DeadlineScheduling(directory, check);
    }

    private static void DeadlineScheduling(string directory, Action<bool, string> check)
    {
        using var db = new FabricDatabase(Path.Combine(directory, "pip-deadline-scheduling", "fabric.db"));
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("deadline-scheduling", "Deadline scheduling").Id;
        var store = new PipStore(db, access);
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));
        var source = store.Register(tenant, "Short appointment", "https://deadline.example.test/attributes", "deadline.example.test", null,
            schemaVersion: 2, maxAgeSeconds: 300, enabled: true, syncSubjects: ["employee"], pollIntervalSeconds: 60);
        var connector = new ScheduledConnector(clock) { ValidForSeconds = 10 };
        var sync = new PipSyncService(store, connector, clock);
        var worker = new PipSyncWorker(store, sync, clock, NullLogger<PipSyncWorker>.Instance);
        check(store.NextDueAt(source, "employee", clock.At) == clock.At && store.NextDueAt(source, "unscheduled", clock.At) is null,
            "shared PIP due calculation makes a new scheduled subject due now and excludes unconfigured subjects");
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1
            && store.NextDueAt(source, "employee", clock.At) == clock.At.AddSeconds(10),
            "successful schema 2 synchronization schedules refresh at a short authoritative deadline");
        clock.At = clock.At.AddSeconds(9);
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 0, "deadline-aware worker does not refresh before the effective due instant");
        clock.At = clock.At.AddSeconds(1);
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1 && connector.Calls == 2,
            "worker refreshes expired appointment facts before the longer configured poll interval");
        clock.At = clock.At.AddSeconds(10); connector.Fail = true;
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1
            && store.NextDueAt(source, "employee", clock.At) == clock.At.AddSeconds(60),
            "failed refresh preserves polling backoff even though the last good source deadline has expired");
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 0, "expired evidence after a failed refresh does not create a retry loop");
        clock.At = clock.At.AddSeconds(60); connector.Fail = false;
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1, "deadline-aware scheduling resumes after the failure backoff");
        var deadline = store.Read(tenant, "employee", clock.At, source.Id).Snapshot!.FreshUntil;
        clock.At = clock.At.AddSeconds(5); connector.NotModified = true;
        var unchanged = sync.SyncAsync(tenant, source.Id, "employee").GetAwaiter().GetResult();
        check(unchanged.Status == "not-modified" && store.NextDueAt(source, "employee", clock.At) == deadline,
            "not-modified checkpoints cannot postpone the original authoritative refresh deadline");
        clock.At = deadline; connector.NotModified = false; connector.ValidForSeconds = 120;
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1
            && store.NextDueAt(source, "employee", clock.At) == clock.At.AddSeconds(60),
            "configured polling remains the earlier limit when fresh source evidence lives longer");
        var oldGenerationRun = store.Checkpoint(tenant, source.Id, "employee")!;
        store.SetEnabled(tenant, source.Id, false);
        check(store.NextDueAt(source, "employee", clock.At) is null,
            "shared due calculation rereads current source enablement instead of scheduling an obsolete enabled definition");
        var reenabled = store.SetEnabled(tenant, source.Id, true);
        clock.At = clock.At.AddSeconds(1);
        check(reenabled.Generation > oldGenerationRun.SourceGeneration
            && store.Checkpoint(tenant, source.Id, "employee") is null
            && store.NextDueAt(source, "employee", clock.At) == clock.At,
            "re-enabled source refreshes immediately instead of inheriting its previous generation's successful poll delay");
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1
            && store.Checkpoint(tenant, source.Id, "employee")?.SourceGeneration == reenabled.Generation,
            "first successful refresh records the current source generation");
        var currentRun = store.Checkpoint(tenant, source.Id, "employee")!;
        var lateOldRun = oldGenerationRun with { Id = Guid.NewGuid(), Status = "failed", Error = "transport-failed",
            CompletedAt = clock.At.AddSeconds(30) };
        store.RecordRun(lateOldRun);
        check(store.Checkpoint(tenant, source.Id, "employee")?.Id == currentRun.Id
            && store.ListRuns(tenant, source.Id).Any(x => x.Id == lateOldRun.Id),
            "late previous-generation completion remains in history without replacing the current checkpoint");
        clock.At = clock.At.AddSeconds(60); connector.Fail = true;
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1
            && store.Checkpoint(tenant, source.Id, "employee") is { Status: "failed" } failed
            && failed.SourceGeneration == reenabled.Generation && store.NextDueAt(source, "employee", clock.At) == clock.At.AddSeconds(60),
            "current-generation failure still receives the full configured retry backoff");
        store.SetEnabled(tenant, source.Id, false);
        var afterFailure = store.SetEnabled(tenant, source.Id, true);
        clock.At = clock.At.AddSeconds(1);
        check(store.NextDueAt(source, "employee", clock.At) == clock.At,
            "re-enabled source does not inherit a previous generation's failed retry delay");
        connector.Fail = false;
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1, "re-enabled failed source obtains evidence for its new generation immediately");
        currentRun = store.Checkpoint(tenant, source.Id, "employee")!;
        var legacyNode = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(currentRun))!.AsObject();
        legacyNode.Remove(nameof(PipSyncRun.SourceGeneration));
        var legacyRun = System.Text.Json.JsonSerializer.Deserialize<PipSyncRun>(legacyNode.ToJsonString())!;
        var subjectKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("employee"))).ToLowerInvariant();
        db.Transaction(() => { db.Put("pip.checkpoint", $"{tenant:D}:{source.Id:D}:{subjectKey}", legacyRun); return true; });
        check(legacyRun.SourceGeneration == 0 && store.Checkpoint(tenant, source.Id, "employee") is null
            && store.NextDueAt(source, "employee", clock.At) == clock.At,
            "legacy unversioned checkpoints deserialize safely and trigger a fresh generation-bound run");
        clock.At = clock.At.AddSeconds(1);
        check(worker.RunDueAsync().GetAwaiter().GetResult() == 1
            && store.Checkpoint(tenant, source.Id, "employee")?.SourceGeneration == afterFailure.Generation,
            "one successful synchronization replaces a legacy checkpoint with the current generation");
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
        public bool NotModified { get; set; }
        public int ValidForSeconds { get; set; } = 10;
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref Calls);
            if (Fail) return Task.FromException<PipFetchedSnapshot>(new HttpRequestException("upstream-unavailable"));
            if (NotModified) return Task.FromException<PipFetchedSnapshot>(new PipNotModifiedException());
            var attributes = new Dictionary<string, string> { ["employment.active"] = "true" };
            var version = "scheduled-" + attempt;
            var at = clock.GetUtcNow();
            DateTimeOffset? until = source.SchemaVersion == 2 ? at.AddSeconds(ValidForSeconds) : null;
            return Task.FromResult(new PipFetchedSnapshot(source.TenantId, subject, source.SchemaVersion, attributes, version, "\"" + version + "\"", at,
                PipStore.HashAttributes(source.TenantId, subject, source.SchemaVersion, attributes, version, at, until), until));
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
