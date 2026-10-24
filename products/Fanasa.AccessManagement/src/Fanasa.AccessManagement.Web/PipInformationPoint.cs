using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Governance;

/// <summary>Configuration for one authoritative external attribute source. Secrets are references only.</summary>
public sealed record PipSourceDefinition(
    Guid Id,
    Guid TenantId,
    string Name,
    string Endpoint,
    string AllowedHost,
    string? SecretReference,
    int SchemaVersion,
    int MaxAgeSeconds,
    int TimeoutSeconds,
    int MaxResponseBytes,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSyncAt = null,
    string? LastSourceVersion = null,
    string? LastEtag = null,
    string Status = "never-synced",
    string? LastError = null, PipAttributeDefinition[]? Definitions = null, string[]? SyncSubjects = null, int PollIntervalSeconds = 0, long Generation = 1, bool Educational = false);

/// <summary>Only normalized security attributes are retained; raw external payloads are deliberately discarded.</summary>
public sealed record PipAttributeSnapshot(
    Guid SourceId,
    Guid TenantId,
    string Subject,
    int SchemaVersion,
    IReadOnlyDictionary<string, string> Attributes,
    string SourceVersion,
    string? Etag,
    DateTimeOffset ObservedAt,
    DateTimeOffset FreshUntil,
    string ContentHash,
    string Provenance, long Generation = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ValidUntil = null);

public sealed record PipSyncRun(
    Guid Id,
    Guid SourceId,
    Guid TenantId,
    string Subject,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? SnapshotHash,
    string? Error, string Actor = "pip-worker", string Reason = "scheduled-sync", long SourceGeneration = 0);

public sealed record PipLookup(PipAttributeSnapshot? Snapshot, string Status, string? Error = null)
{
    public bool IsFresh => Status == "fresh" && Snapshot is not null;
}

public interface IPolicyInformationPoint
{
    PipLookup Read(Guid tenantId, string subject, DateTimeOffset now, Guid? sourceId = null);
}

public interface IPipExternalConnector
{
    Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default);
}

/// <summary>Resolves a stored reference through deployment secrets; the resolved credential never enters Fabric.</summary>
public interface IPipSecretResolver
{
    ValueTask<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default);
}

public sealed class ConfigurationPipSecretResolver(IConfiguration configuration) : IPipSecretResolver
{
    public ValueTask<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return ValueTask.FromResult<string?>(null);
        var value = configuration[$"Pip:Secrets:{reference}"];
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(value) ? null : value);
    }
}

public sealed record PipFetchedSnapshot(
    Guid TenantId,
    string Subject,
    int SchemaVersion,
    IReadOnlyDictionary<string, string> Attributes,
    string SourceVersion,
    string? Etag,
    DateTimeOffset ObservedAt,
    string ContentHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ValidUntil = null);

/// <summary>Fabric-backed PIP catalog and snapshot store. Every read is tenant-scoped.</summary>
public sealed partial class PipStore(FabricDatabase database, PersistentAccessManagement access) : IPolicyInformationPoint
{
    public const string SourceDocumentKind = "pip.source";
    public const string SnapshotDocumentKind = "pip.snapshot";
    public const string SyncDocumentKind = "pip.sync";

    public PipSourceDefinition[] ListSources(Guid tenantId)
    {
        EnsureTenant(tenantId);
        return database.All<PipSourceDefinition>(SourceDocumentKind).Where(x => x.TenantId == tenantId).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    public PipSourceDefinition ReadSource(Guid tenantId, Guid sourceId)
    {
        EnsureTenant(tenantId);
        var source = database.Read<PipSourceDefinition>(SourceDocumentKind, sourceId.ToString("D"));
        if (source is null || source.TenantId != tenantId) throw new KeyNotFoundException("PIP source was not found.");
        return source;
    }

    public PipSourceDefinition Register(Guid tenantId, string name, string endpoint, string allowedHost,
        string? secretReference, int schemaVersion = 1, int maxAgeSeconds = 300, int timeoutSeconds = 10,
        int maxResponseBytes = 262144, bool enabled = false, PipAttributeDefinition[]? definitions = null,
        string[]? syncSubjects = null, int pollIntervalSeconds = 0, string actor = "system", string reason = "source-registration", bool educational = false)
    {
        EnsureTenant(tenantId); ValidateEndpoint(endpoint, allowedHost);
        ValidateLimits(schemaVersion, maxAgeSeconds, timeoutSeconds, maxResponseBytes);
        AccessPolicyStore.Label(name, 200); AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000);
        if (secretReference is not null && (secretReference.Length > 200 || secretReference.Any(char.IsControl))) throw new ArgumentException("Invalid secret reference.");
        definitions ??= PipSchema.Default; PipSchema.Validate(definitions);
        syncSubjects ??= [];
        if (syncSubjects.Length > 100 || syncSubjects.Distinct(StringComparer.Ordinal).Count() != syncSubjects.Length) throw new ArgumentException("At most 100 unique PIP subjects per source.");
        foreach (var subject in syncSubjects) ValidateSubject(subject);
        if (pollIntervalSeconds != 0 && (pollIntervalSeconds < 30 || pollIntervalSeconds > 86400 || pollIntervalSeconds >= maxAgeSeconds || syncSubjects.Length == 0))
            throw new ArgumentException("Polling must be 30..86400 seconds, less than maximum age, with explicit subjects.");
        var source = new PipSourceDefinition(Guid.NewGuid(), tenantId, name.Trim(), endpoint.Trim(), allowedHost.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(secretReference) ? null : secretReference.Trim(), schemaVersion, maxAgeSeconds, timeoutSeconds,
            maxResponseBytes, enabled, DateTimeOffset.UtcNow, Definitions: definitions.ToArray(), SyncSubjects: syncSubjects.ToArray(), PollIntervalSeconds: pollIntervalSeconds, Educational: educational);
        return database.Transaction(() =>
        {
            if (ListSources(tenantId).Length >= 50) throw new InvalidOperationException("At most 50 PIP sources per tenant.");
            database.Put(SourceDocumentKind, source.Id.ToString("D"), source);
            database.Emit("pip-source:" + source.Id, "PipSourceRegistered", new { source.TenantId, source.Id, Actor = actor, Reason = reason }, DateTimeOffset.UtcNow);
            return source;
        });
    }
    public PipSourceDefinition SetEnabled(Guid tenantId, Guid sourceId, bool enabled, string actor = "system", string reason = "source-state-change") => database.Transaction(() =>
    {
        AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000);
        var source = ReadSource(tenantId, sourceId);
        if (source.Enabled == enabled) return source;
        var updated = source with { Enabled = enabled, Generation = checked(source.Generation + 1), Status = enabled ? "never-synced" : "disabled", LastError = null, LastEtag = null };
        database.Put(SourceDocumentKind, source.Id.ToString("D"), updated);
        database.Emit("pip-state:" + Guid.NewGuid(), "PipSourceStateChanged", new { source.TenantId, source.Id, enabled, updated.Generation, Actor = actor, Reason = reason }, DateTimeOffset.UtcNow);
        return updated;
    });
    public PipLookup Read(Guid tenantId, string subject, DateTimeOffset now, Guid? sourceId = null)
    {
        PipLookup Core()
        {
            EnsureTenant(tenantId); ValidateSubject(subject);
            var sources = ListSources(tenantId).Where(x => x.Enabled && (!sourceId.HasValue || x.Id == sourceId)).ToArray();
            if (sources.Length == 0) return new(null, "missing", "no-enabled-source");
            if (sources.Length > 1) return new(null, "invalid", "ambiguous-source");
            var source = sources[0];
            try
            {
                var snapshot = database.Read<PipAttributeSnapshot>(SnapshotDocumentKind, SnapshotKey(tenantId, subject, source.Id));
                if (snapshot is null) return new(null, "missing", "no-snapshot");
                ValidateSnapshot(new(snapshot.TenantId, snapshot.Subject, snapshot.SchemaVersion, snapshot.Attributes,
                    snapshot.SourceVersion, snapshot.Etag, snapshot.ObservedAt, snapshot.ContentHash, snapshot.ValidUntil), source, now);
                if (snapshot.SourceId != source.Id || snapshot.TenantId != tenantId || snapshot.Subject != subject || snapshot.Generation != source.Generation
                    || snapshot.FreshUntil != FreshUntil(source, new(snapshot.TenantId, snapshot.Subject, snapshot.SchemaVersion, snapshot.Attributes,
                        snapshot.SourceVersion, snapshot.Etag, snapshot.ObservedAt, snapshot.ContentHash, snapshot.ValidUntil))
                    || snapshot.Provenance != Provenance(source.Id, snapshot.SourceVersion)) return new(null, "invalid", "invalid-snapshot-metadata");
                return snapshot.FreshUntil > now ? new(snapshot, "fresh") : new(snapshot, "stale", "snapshot-expired");
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or OverflowException)
            { return new(null, "invalid", "invalid-snapshot"); }
        }
        return database.IsInTransaction ? Core() : database.ReadSnapshot(Core);
    }
    public PipAttributeSnapshot SaveSnapshot(PipSourceDefinition source, string expectedSubject, PipFetchedSnapshot fetched, DateTimeOffset now)
    {
        ValidateFetchedSnapshot(source, expectedSubject, fetched, now);
        return database.Transaction(() =>
        {
            var current = ReadSource(source.TenantId, source.Id);
            if (!current.Enabled || current.Generation != source.Generation || current.Endpoint != source.Endpoint || current.SchemaVersion != source.SchemaVersion)
                throw new InvalidOperationException("PIP source changed during synchronization.");
            ValidateSnapshot(fetched, current, now);
            var key = SnapshotKey(source.TenantId, fetched.Subject, source.Id);
            var existing = database.Read<PipAttributeSnapshot>(SnapshotDocumentKind, key);
            var versionKey = key + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fetched.SourceVersion)));
            var receipt = database.Read<PipVersionReceipt>("pip.version", versionKey);
            if (receipt is not null && (receipt.ContentHash != fetched.ContentHash || receipt.ObservedAt != fetched.ObservedAt)
                || existing is not null && existing.SourceVersion == fetched.SourceVersion && existing.ContentHash != fetched.ContentHash)
                throw new InvalidDataException("A PIP source version was replayed with different content.");
            if (existing is not null && existing.SourceVersion != fetched.SourceVersion && fetched.ObservedAt <= existing.ObservedAt)
                throw new InvalidDataException("An older PIP observation cannot replace the current snapshot.");
            var snapshot = new PipAttributeSnapshot(source.Id, source.TenantId, fetched.Subject, fetched.SchemaVersion,
                new Dictionary<string, string>(fetched.Attributes, StringComparer.Ordinal), fetched.SourceVersion, fetched.Etag,
                fetched.ObservedAt, FreshUntil(current, fetched), fetched.ContentHash, Provenance(source.Id, fetched.SourceVersion), current.Generation, fetched.ValidUntil);
            database.Put(SourceDocumentKind, source.Id.ToString("D"), current with { LastSyncAt = now, LastSourceVersion = fetched.SourceVersion, LastEtag = null, Status = "healthy", LastError = null });
            database.Put(SnapshotDocumentKind, key, snapshot);
            database.Put("pip.version", versionKey, new PipVersionReceipt(fetched.ContentHash, fetched.ObservedAt));
            if (existing is null || existing.ContentHash != snapshot.ContentHash || existing.Generation != snapshot.Generation)
                database.Emit("pip-snapshot:" + Guid.NewGuid(), "PipSnapshotStored", new { source.TenantId, source.Id, fetched.Subject, fetched.SourceVersion, fetched.ContentHash, current.Generation }, now);
            return snapshot;
        });
    }
    // Compatibility overload for trusted adapters that already correlated the subject.
    public PipAttributeSnapshot SaveSnapshot(PipSourceDefinition source, PipFetchedSnapshot fetched, DateTimeOffset now) => SaveSnapshot(source, fetched.Subject, fetched, now);

    public void RecordFailure(PipSourceDefinition source, DateTimeOffset at, string error) => database.Transaction(() =>
    {
        var safe = error is "transport-failed" or "timeout" or "invalid-response" or "source-unavailable" or "cancelled" ? error : "sync-failed";
        var current = ReadSource(source.TenantId, source.Id);
        if (current.Generation == source.Generation && current.Enabled)
            database.Put(SourceDocumentKind, source.Id.ToString("D"), current with { LastSyncAt = at, Status = "error", LastError = safe });
        return true;
    });
    public void RecordRun(PipSyncRun run) => database.Transaction(() =>
    {
        var source = ReadSource(run.TenantId, run.SourceId);
        ValidateSubject(run.Subject); AccessPolicyStore.Label(run.Actor, 200); AccessPolicyStore.Label(run.Reason, 1000);
        if (run.Id == Guid.Empty || run.CompletedAt < run.StartedAt || run.CompletedAt.Offset != TimeSpan.Zero || run.StartedAt.Offset != TimeSpan.Zero
            || run.SourceGeneration < 0 || run.SourceGeneration > source.Generation)
            throw new ArgumentException("Invalid synchronization receipt.");
        database.Put(SyncDocumentKind, run.Id.ToString("D"), run);
        var key = SnapshotKey(run.TenantId, run.Subject, run.SourceId);
        var previous = database.Read<PipSyncRun>("pip.checkpoint", key);
        // Keep superseded runs in history, but they cannot suppress refresh or overwrite a new generation's checkpoint.
        var currentGeneration = run.SourceGeneration == source.Generation || source.Educational && run.SourceGeneration == 0;
        if (currentGeneration && (previous is null || previous.SourceGeneration != run.SourceGeneration || run.CompletedAt >= previous.CompletedAt))
            database.Put("pip.checkpoint", key, run);
        database.Emit("pip-run:" + run.Id, "PipSyncCompleted", run, run.CompletedAt); return true;
    });
    public PipSyncRun[] ListRuns(Guid tenantId, Guid sourceId, int take = 50)
    {
        _ = ReadSource(tenantId, sourceId);
        if (take is < 1 or > 100) throw new ArgumentException("Invalid PIP run limit.");
        return database.All<PipSyncRun>(SyncDocumentKind).Where(x => x.TenantId == tenantId && x.SourceId == sourceId)
            .OrderByDescending(x => x.CompletedAt).Take(take).ToArray();
    }

    private void EnsureTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty || !access.GetTenants().Any(x => x.Id == tenantId && x.IsActive)) throw new KeyNotFoundException("Tenant was not found.");
    }
    private static void ValidateSubject(string subject) { AccessPolicyStore.Label(subject, 200); if (subject.Any(char.IsControl)) throw new ArgumentException("Invalid PIP subject."); }
    private static void ValidateLimits(int schemaVersion, int maxAgeSeconds, int timeoutSeconds, int maxResponseBytes)
    {
        if (schemaVersion is not (1 or 2) || maxAgeSeconds is < 1 or > 86400 || timeoutSeconds is < 1 or > 120 || maxResponseBytes is < 1024 or > 10 * 1024 * 1024)
            throw new ArgumentException("Invalid PIP source limits.");
    }
    internal static void ValidateEndpoint(string endpoint, string allowedHost)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || (!string.IsNullOrWhiteSpace(uri.Fragment) || uri.Query.Length > 0))
            throw new ArgumentException("PIP endpoint must be an HTTPS URL without credentials or fragments.");
        // Network authorization belongs to deployment-owned PipTransportOptions and its pinned DNS connector.
        _ = PipTransportOptions.CanonicalEndpoint(endpoint);
        AccessPolicyStore.Label(allowedHost, 253);
        if (!string.Equals(uri.Host, allowedHost.Trim(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Endpoint host must match the allowlist.");
    }
    private static string SnapshotKey(Guid tenantId, string subject, Guid sourceId) => $"{tenantId:D}:{sourceId:D}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant()}";
    private static string Provenance(Guid source, string version) => $"pip://{source:D}/{Uri.EscapeDataString(version)}";
    public static DateTimeOffset FreshUntil(PipSourceDefinition source, PipFetchedSnapshot fetched)
    {
        var ttl = fetched.ObservedAt.AddSeconds(source.MaxAgeSeconds);
        return fetched.ValidUntil is { } deadline && deadline < ttl ? deadline : ttl;
    }
    public static void ValidateFetchedSnapshot(PipSourceDefinition source, string expectedSubject, PipFetchedSnapshot fetched, DateTimeOffset now)
    {
        ValidateSubject(expectedSubject);
        if (fetched.TenantId != source.TenantId || fetched.Subject != expectedSubject) throw new InvalidDataException("External PIP response correlation failed.");
        ValidateSnapshot(fetched, source, now);
        if (FreshUntil(source, fetched) <= now) throw new InvalidDataException("External PIP response is already stale.");
    }
    private static void ValidateSnapshot(PipFetchedSnapshot fetched, PipSourceDefinition source, DateTimeOffset now)
    {
        ValidateLimits(source.SchemaVersion, source.MaxAgeSeconds, source.TimeoutSeconds, source.MaxResponseBytes);
        ValidateSubject(fetched.Subject);
        if (source.Generation < 1 || fetched.TenantId != source.TenantId || fetched.SchemaVersion != source.SchemaVersion || string.IsNullOrWhiteSpace(fetched.SourceVersion)
            || fetched.SourceVersion.Length > 200 || fetched.SourceVersion.Any(char.IsControl) || fetched.ObservedAt.Offset != TimeSpan.Zero
            || fetched.ObservedAt > now || fetched.ObservedAt == DateTimeOffset.MinValue) throw new InvalidDataException("Invalid PIP response metadata.");
        if (fetched.SchemaVersion == 1 && fetched.ValidUntil is not null
            || fetched.SchemaVersion == 2 && (fetched.ValidUntil is not { } deadline || deadline.Offset != TimeSpan.Zero || deadline <= fetched.ObservedAt))
            throw new InvalidDataException("PIP schema 2 requires an explicit UTC deadline after observation; schema 1 cannot carry a deadline.");
        PipSchema.ValidateAttributes(source, fetched.Attributes);
        var hash = HashAttributes(fetched.TenantId, fetched.Subject, fetched.SchemaVersion, fetched.Attributes, fetched.SourceVersion, fetched.ObservedAt, fetched.ValidUntil);
        if (hash != fetched.ContentHash) throw new InvalidDataException("PIP content hash mismatch.");
    }
    public static string HashAttributes(Guid tenantId, string subject, int schemaVersion, IReadOnlyDictionary<string, string> attributes, string sourceVersion, DateTimeOffset observedAt, DateTimeOffset? validUntil = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); writer.WriteStringValue(tenantId.ToString("D")); writer.WriteStringValue(subject); writer.WriteNumberValue(schemaVersion);
            writer.WriteStringValue(sourceVersion); writer.WriteStringValue(observedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            if (schemaVersion == 2) writer.WriteStringValue(validUntil?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteStartObject(); foreach (var pair in attributes.OrderBy(x => x.Key, StringComparer.Ordinal)) writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject(); writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
public sealed record PipVersionReceipt(string ContentHash, DateTimeOffset ObservedAt);
public sealed record PipSyncLease(Guid RunId, DateTimeOffset ExpiresAt);
public sealed class PipSyncService(PipStore store, IPipExternalConnector connector, TimeProvider clock)
{
    public async Task<PipSyncRun> SyncAsync(Guid tenantId, Guid sourceId, string subject, CancellationToken cancellationToken = default,
        string actor = "pip-worker", string reason = "scheduled-sync")
    {
        AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000); AccessPolicyStore.Label(subject, 200);
        var source = store.ReadSource(tenantId, sourceId);
        if (!source.Enabled || source.Educational) throw new InvalidOperationException("PIP source is disabled or educational.");
        var started = clock.GetUtcNow(); var runId = Guid.NewGuid();
        if (!store.TryLease(tenantId, sourceId, subject, runId, started)) throw new InvalidOperationException("This PIP subject is already synchronizing.");
        try
        {
            var cached = store.Read(tenantId, subject, started, sourceId);
            var requestSource = source with { LastEtag = cached.IsFresh ? cached.Snapshot?.Etag : null };
            PipAttributeSnapshot snapshot;
            try
            {
                var fetched = await connector.FetchAsync(requestSource, subject, cancellationToken).ConfigureAwait(false);
                snapshot = store.SaveSnapshot(source, subject, fetched, clock.GetUtcNow());
            }
            catch (PipNotModifiedException)
            {
                var current = store.Read(tenantId, subject, clock.GetUtcNow(), sourceId);
                if (requestSource.LastEtag is null || !current.IsFresh || current.Snapshot?.Etag != requestSource.LastEtag || current.Snapshot.Generation != source.Generation)
                    throw new InvalidDataException("Not-modified response has no fresh correlated snapshot.");
                snapshot = current.Snapshot;
                store.SaveSnapshot(source, subject, new(snapshot.TenantId, subject, snapshot.SchemaVersion, snapshot.Attributes, snapshot.SourceVersion,
                    snapshot.Etag, snapshot.ObservedAt, snapshot.ContentHash, snapshot.ValidUntil), clock.GetUtcNow());
                return Complete("not-modified", snapshot.ContentHash, null);
            }
            return Complete("succeeded", snapshot.ContentHash, null);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException or IOException)
        {
            var safe = cancellationToken.IsCancellationRequested ? "cancelled" : error switch
            { OperationCanceledException => "timeout", HttpRequestException => "transport-failed", InvalidOperationException => "source-unavailable", _ => "invalid-response" };
            store.RecordFailure(source, clock.GetUtcNow(), safe);
            var run = Complete(cancellationToken.IsCancellationRequested ? "cancelled" : "failed", null, safe);
            cancellationToken.ThrowIfCancellationRequested(); return run;
        }
        finally { store.ReleaseLease(tenantId, sourceId, subject, runId); }
        PipSyncRun Complete(string status, string? hash, string? error)
        {
            var run = new PipSyncRun(runId, sourceId, tenantId, subject, status, started, clock.GetUtcNow(), hash, error, actor, reason, source.Generation);
            store.RecordRun(run); return run;
        }
    }
}
