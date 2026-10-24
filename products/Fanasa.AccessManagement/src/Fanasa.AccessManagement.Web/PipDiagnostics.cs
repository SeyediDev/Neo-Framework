using System.Security.Cryptography;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Governance;

public sealed record PipAttributeView(string Name, string ExternalName, string DataType, bool Required, string? Value);
public sealed record PipSetupState(string State, string? BlockingCode, string NextAction, DateTimeOffset? TestedAt,
    DateTimeOffset? TestExpiresAt, long? TestedGeneration, bool CanEnable);
public sealed record PipTestReceipt(Guid Id, Guid TenantId, Guid SourceId, string Subject, long Generation,
    string ConfigurationHash, string BindingHash, string Status, string? ErrorCode, DateTimeOffset TestedAt,
    DateTimeOffset ExpiresAt, string? SourceVersion, string? ContentHash, string Actor, string Reason);
public sealed record PipTestResult(string Status, string? ErrorCode, DateTimeOffset TestedAt, DateTimeOffset ExpiresAt,
    long Generation, Guid ReceiptId, PipAttributeView[] Attributes, string? SourceVersion,
    DateTimeOffset? ObservedAt, bool CanEnable, bool SnapshotStored = false);
public sealed record PipSubjectStatus(string Subject, Guid SourceId, long Generation, DateTimeOffset EvaluatedAt,
    string Status, string? Error, DateTimeOffset? ObservedAt, DateTimeOffset? FreshUntil, string? SourceVersion,
    string? ContentHash, bool DeploymentApproved, PipSyncRun? Checkpoint, DateTimeOffset? FetchedAt,
    string? Provenance, bool Usable, PipAttributeView[] Attributes, string? BlockingCode, string NextAction,
    PipSyncRun? LastRun, DateTimeOffset? NextDueAt);
public sealed record PipOverviewCounts(int Fresh, int Stale, int Missing, int Invalid);
public sealed record PipSourceOverview(PipSourceDefinition Source, bool DeploymentApproved, PipSetupState Setup,
    PipSubjectStatus[] Subjects, PipOverviewCounts Counts);
public sealed record PipOverview(DateTimeOffset EvaluatedAt, PipSourceOverview[] Sources);

public sealed partial class PipStore
{
    internal static string ConfigurationHash(PipSourceDefinition source) => HashConfiguration(new
    {
        source.Id, source.TenantId, source.Endpoint, source.AllowedHost, source.SecretReference, source.SchemaVersion,
        source.MaxAgeSeconds, source.TimeoutSeconds, source.MaxResponseBytes, source.Enabled, source.Generation,
        source.Definitions, source.SyncSubjects, source.PollIntervalSeconds, source.Educational
    });
    internal static string HashConfiguration<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
    internal PipTestReceipt? TestReceipt(Guid tenantId, Guid sourceId)
    {
        _ = ReadSource(tenantId, sourceId);
        var receipt = database.Read<PipTestReceipt>("pip.test", sourceId.ToString("D"));
        return receipt?.TenantId == tenantId && receipt.SourceId == sourceId ? receipt : null;
    }
    internal bool AcquireTestLease(Guid tenantId, Guid sourceId, Guid runId, DateTimeOffset now, int seconds) => database.Transaction(() =>
    {
        _ = ReadSource(tenantId, sourceId);
        var key = sourceId.ToString("D");
        var lease = database.Read<PipSyncLease>("pip.test-lease", key);
        if (lease is not null && lease.ExpiresAt > now) return false;
        database.Put("pip.test-lease", key, new PipSyncLease(runId, now.AddSeconds(seconds + 30)));
        return true;
    });
    internal void ReleaseTestLease(Guid sourceId, Guid runId, DateTimeOffset now) => database.Transaction(() =>
    {
        var key = sourceId.ToString("D");
        if (database.Read<PipSyncLease>("pip.test-lease", key)?.RunId == runId)
            database.Put("pip.test-lease", key, new PipSyncLease(runId, now));
        return true;
    });
    internal bool SaveTestReceipt(PipSourceDefinition source, PipTestReceipt receipt) => database.Transaction(() =>
    {
        if (ConfigurationHash(ReadSource(source.TenantId, source.Id)) != receipt.ConfigurationHash) return false;
        database.Put("pip.test", source.Id.ToString("D"), receipt);
        database.Emit("pip-test:" + receipt.Id, "PipConnectionTested", receipt, receipt.TestedAt);
        return true;
    });
    public PipSourceDefinition EnableAfterTest(Guid tenantId, Guid sourceId, bool enabled, string actor, string reason,
        PipTransportOptions transport, DateTimeOffset now) => database.Transaction(() =>
    {
        var source = ReadSource(tenantId, sourceId);
        if (enabled)
        {
            var binding = transport.Authorize(source);
            if (!source.Enabled && !ValidTest(source, TestReceipt(tenantId, sourceId), binding, now))
                throw new InvalidOperationException("A successful, unexpired connection test of the current source configuration is required before enabling it.");
        }
        return SetEnabled(tenantId, sourceId, enabled, actor, reason);
    });
    internal static bool ValidTest(PipSourceDefinition source, PipTestReceipt? receipt, PipEndpointBinding binding, DateTimeOffset now) =>
        receipt is not null && receipt.TenantId == source.TenantId && receipt.SourceId == source.Id && receipt.Status == "succeeded"
        && receipt.TestedAt <= now && receipt.ExpiresAt > now && receipt.ExpiresAt <= receipt.TestedAt.AddMinutes(5)
        && receipt.Generation == source.Generation && receipt.ConfigurationHash == ConfigurationHash(source)
        && receipt.BindingHash == HashConfiguration(binding);
}

/// <summary>Explicit connection testing and read-only diagnostics; tests never create authorization snapshots.</summary>
public sealed class PipDiagnostics(PipStore store, IPipExternalConnector connector, PipTransportOptions transport,
    FabricDatabase database, TimeProvider clock)
{
    private readonly SemaphoreSlim testSlots = new(4, 4);
    public PipSetupState Setup(PipSourceDefinition source, DateTimeOffset now)
    {
        if (source.Educational) return new("educational", null, "simulate", null, null, null, false);
        var receipt = store.TestReceipt(source.TenantId, source.Id);
        if (!transport.IsAuthorized(source)) return new("deployment-required", "deployment-binding-required", "configure-deployment-binding", receipt?.TestedAt, receipt?.ExpiresAt, receipt?.Generation, false);
        if (source.Enabled) return new("enabled", null, "sync-subject", receipt?.TestedAt, receipt?.ExpiresAt, receipt?.Generation, false);
        var valid = PipStore.ValidTest(source, receipt, transport.Authorize(source), now);
        return new(valid ? "tested" : "test-required", valid ? null : "valid-connection-test-required", valid ? "enable-source" : "test-connection",
            receipt?.TestedAt, receipt?.ExpiresAt, receipt?.Generation, valid);
    }
    public PipOverview Overview(Guid tenantId) => database.ReadSnapshot(() =>
    {
        var now = clock.GetUtcNow();
        return new PipOverview(now, store.ListSources(tenantId).Take(50).Select(source =>
        {
            var subjects = (source.SyncSubjects ?? []).Take(100).Select(subject => StatusCore(source, subject, now)).ToArray();
            return new PipSourceOverview(source, source.Educational || transport.IsAuthorized(source), Setup(source, now), subjects,
                new(subjects.Count(x => x.Status == "fresh"), subjects.Count(x => x.Status == "stale"),
                    subjects.Count(x => x.Status == "missing"), subjects.Count(x => x.Status == "invalid")));
        }).ToArray());
    });
    public PipSubjectStatus Status(Guid tenantId, Guid sourceId, string subject) => database.ReadSnapshot(() =>
        StatusCore(store.ReadSource(tenantId, sourceId), subject, clock.GetUtcNow()));
    public PipSubjectStatus StatusAt(Guid tenantId, Guid sourceId, string subject, DateTimeOffset at)
    {
        PipSubjectStatus Core() => StatusCore(store.ReadSource(tenantId, sourceId), subject, at.ToUniversalTime(), includeSchedule: false);
        return database.IsInTransaction ? Core() : database.ReadSnapshot(Core);
    }
    private PipSubjectStatus StatusCore(PipSourceDefinition source, string subject, DateTimeOffset now, bool includeSchedule = true)
    {
        var lookup = store.Read(source.TenantId, subject, now, source.Id);
        var snapshot = lookup.Snapshot;
        var run = store.Checkpoint(source.TenantId, source.Id, subject);
        if (run?.CompletedAt > now) run = null;
        var approved = source.Educational || transport.IsAuthorized(source);
        // Deployment approval gates new network requests. Existing validated evidence keeps its original deadline;
        // disabling a source explicitly invalidates it by generation. Match the PDP's evidence semantics here.
        var usable = lookup.IsFresh;
        var code = !approved ? "deployment-binding-required" : !source.Enabled ? "source-disabled" : lookup.Error;
        var next = !approved ? "configure-deployment-binding" : !source.Enabled ? Setup(source, now).NextAction
            : lookup.IsFresh ? "simulate-policy" : "sync-subject";
        var nextDue = includeSchedule ? store.NextDueAt(source, subject, now) : null;
        return new(subject, source.Id, source.Generation, now, lookup.Status, lookup.Error, snapshot?.ObservedAt, snapshot?.FreshUntil,
            snapshot?.SourceVersion, snapshot?.ContentHash, approved, run,
            run?.Status is "succeeded" or "not-modified" && run.SnapshotHash == snapshot?.ContentHash ? run.CompletedAt : null,
            snapshot?.Provenance, usable, lookup.Status == "invalid" ? [] : Attributes(source, snapshot?.Attributes), code, next, run, nextDue);
    }
    internal static PipAttributeView[] Attributes(PipSourceDefinition source, IReadOnlyDictionary<string, string>? attributes) =>
        (source.Definitions ?? PipSchema.Default).Select(definition => new PipAttributeView(definition.Name, definition.ExternalName ?? definition.Name,
            definition.DataType, definition.Required, attributes?.GetValueOrDefault(definition.Name))).ToArray();

    public async Task<PipTestResult> TestAsync(Guid tenantId, Guid sourceId, string subject, string actor, string reason,
        CancellationToken cancellationToken = default)
    {
        AccessPolicyStore.Label(subject, 200); AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000);
        if (subject.Any(char.IsControl)) throw new ArgumentException("Invalid PIP subject.");
        var source = store.ReadSource(tenantId, sourceId);
        if (source.Educational) throw new InvalidOperationException("Educational sources use synthetic fixtures and cannot test external connections.");
        var bindingHash = PipStore.HashConfiguration(transport.Authorize(source));
        if (!await testSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Connection test capacity is busy; retry shortly.");
        var runId = Guid.NewGuid(); var lease = false;
        try
        {
            lease = store.AcquireTestLease(tenantId, sourceId, runId, clock.GetUtcNow(), source.TimeoutSeconds);
            if (!lease) throw new InvalidOperationException("This source already has a connection test in progress.");
            PipFetchedSnapshot? fetched = null; string? error = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
                fetched = await connector.FetchAsync(source with { Enabled = true, LastEtag = null }, subject, timeout.Token).ConfigureAwait(false);
                PipStore.ValidateFetchedSnapshot(source, subject, fetched, clock.GetUtcNow());
            }
            catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException or IOException)
            {
                fetched = null;
                error = cancellationToken.IsCancellationRequested ? "cancelled" : failure switch
                { OperationCanceledException => "timeout", HttpRequestException => "transport-failed", InvalidOperationException => "source-unavailable", _ => "invalid-response" };
            }
            var now = clock.GetUtcNow();
            if (!transport.IsAuthorized(source) || bindingHash != PipStore.HashConfiguration(transport.Authorize(source)))
            { fetched = null; error = "deployment-binding-changed"; }
            var expires = fetched is null ? now : new[] { now.AddMinutes(5), PipStore.FreshUntil(source, fetched) }.Min();
            var receipt = new PipTestReceipt(runId, tenantId, sourceId, subject, source.Generation, PipStore.ConfigurationHash(source), bindingHash,
                fetched is null ? "failed" : "succeeded", error, now, expires, fetched?.SourceVersion, fetched?.ContentHash, actor, reason);
            if (!store.SaveTestReceipt(source, receipt))
                return new("failed", "source-changed", now, now, source.Generation, runId, [], null, null, false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(receipt.Status, error, now, expires, source.Generation, runId, fetched is null ? [] : Attributes(source, fetched.Attributes),
                fetched?.SourceVersion, fetched?.ObservedAt, fetched is not null && !source.Enabled && expires > now);
        }
        finally
        {
            if (lease) store.ReleaseTestLease(sourceId, runId, clock.GetUtcNow());
            testSlots.Release();
        }
    }
}
