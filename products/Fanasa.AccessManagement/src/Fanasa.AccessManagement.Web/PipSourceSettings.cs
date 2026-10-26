namespace Fanasa.AccessManagement.Web.Governance;

/// <summary>A stable conflict code for an operator correction; no supplied configuration is echoed.</summary>
public sealed class PipSourceSettingsConflictException : InvalidOperationException
{
    public string ErrorCode { get; }
    internal PipSourceSettingsConflictException(string errorCode)
        : base("PIP source settings cannot be changed in the current state.") => ErrorCode = errorCode;
}

public sealed partial class PipStore
{
    /// <summary>Changes polling and freshness settings without replacing the source's authority contract.</summary>
    public PipSourceDefinition UpdateSettings(Guid tenantId, Guid sourceId, long expectedGeneration, string name,
        int maxAgeSeconds = 300, int timeoutSeconds = 10, int maxResponseBytes = 262144,
        string[]? syncSubjects = null, int pollIntervalSeconds = 0, string actor = "system", string reason = "source-settings-change")
        => database.Transaction(() =>
        {
            if (expectedGeneration < 1) throw new ArgumentException("A positive PIP source generation is required.");
            AccessPolicyStore.Label(name, 200); AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000);
            var source = ReadSource(tenantId, sourceId);
            if (source.Educational) throw new PipSourceSettingsConflictException("educational-source-read-only");
            if (source.Generation != expectedGeneration) throw new PipSourceSettingsConflictException("source-generation-conflict");
            if (source.Enabled) throw new PipSourceSettingsConflictException("source-must-be-disabled");
            ValidateLimits(source.SchemaVersion, maxAgeSeconds, timeoutSeconds, maxResponseBytes);
            var subjects = syncSubjects?.ToArray() ?? [];
            if (subjects.Length > 100 || subjects.Distinct(StringComparer.Ordinal).Count() != subjects.Length)
                throw new ArgumentException("At most 100 unique PIP subjects per source.");
            foreach (var subject in subjects) ValidateSubject(subject);
            if (pollIntervalSeconds != 0 && (pollIntervalSeconds < 30 || pollIntervalSeconds > 86400
                || pollIntervalSeconds >= maxAgeSeconds || subjects.Length == 0))
                throw new ArgumentException("Polling must be 30..86400 seconds, less than maximum age, with explicit subjects.");
            var updated = source with
            {
                Name = name.Trim(), MaxAgeSeconds = maxAgeSeconds, TimeoutSeconds = timeoutSeconds,
                MaxResponseBytes = maxResponseBytes, SyncSubjects = subjects, PollIntervalSeconds = pollIntervalSeconds,
                Generation = checked(source.Generation + 1), Enabled = false, Status = "disabled",
                LastSyncAt = null, LastSourceVersion = null, LastEtag = null, LastError = null
            };
            database.Put(SourceDocumentKind, source.Id.ToString("D"), updated);
            database.Emit("pip-settings:" + Guid.NewGuid(), "PipSourceSettingsChanged", new
            {
                source.TenantId, source.Id, PreviousGeneration = source.Generation, updated.Generation, Actor = actor, Reason = reason
            }, DateTimeOffset.UtcNow);
            return updated;
        });
}
