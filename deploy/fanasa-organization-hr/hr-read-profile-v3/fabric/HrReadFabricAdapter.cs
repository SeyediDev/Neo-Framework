using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.HrReadCandidate.Fabric;

/// <summary>Immutable request evidence only; constructing this type grants no authority.</summary>
public class HrReadFabricAdmission : HrReadAdmission
{
    // Local allocation ceiling, not an approved operational payload allowance.
    public const int AbsoluteMaximumSourceDocumentBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public HrReadFabricAdmission(HrReadTarget target, int maximumResponseBytes, DateTimeOffset validUntil,
        string tier, string requestId, string keyId, decimal expiresAt, int maximumSourceDocumentBytes)
        : base(target, maximumResponseBytes, validUntil)
    {
        if (target.TenantId == Guid.Empty || target.Kind is not (HrReadEndpointKind.Chart or HrReadEndpointKind.History)
            || target.Kind == HrReadEndpointKind.Chart && target.Revision.HasValue
            || target.Kind == HrReadEndpointKind.History && target.Revision is not > 0)
            throw new ArgumentException("Invalid HR read target.", nameof(target));
        if (tier is not ("edge" or "internal")) throw new ArgumentException("Invalid gateway tier.", nameof(tier));
        if (requestId is null || requestId.Length != 34 || !requestId.StartsWith("g-", StringComparison.Ordinal)
            || !requestId.AsSpan(2).ToArray().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Invalid gateway request identifier.", nameof(requestId));
        if (!Identifier(keyId, 64) || keyId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Invalid gateway key identifier.", nameof(keyId));
        if (expiresAt is <= 0 or > 253402300799m) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if (maximumSourceDocumentBytes is <= 0 or > AbsoluteMaximumSourceDocumentBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumSourceDocumentBytes));
        Tier = tier; RequestId = requestId; KeyId = keyId; ExpiresAt = expiresAt;
        MaximumSourceDocumentBytes = maximumSourceDocumentBytes;
    }
    public string Tier { get; }
    public string RequestId { get; }
    public string KeyId { get; }
    public decimal ExpiresAt { get; }
    public int MaximumSourceDocumentBytes { get; }
    internal static decimal Epoch(DateTimeOffset instant) => (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10000000m;
    private static bool Identifier(string? value, int maximumBytes)
    {
        try
        {
            return value is { Length: > 0 } && value.Length <= maximumBytes
                && !value.Any(character => char.IsWhiteSpace(character) || character < 32 || character == 127)
                && StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException) { return false; }
    }
}

/// <summary>
/// Host authority adapter, synchronously invoked inside the supplied existing Fabric context.
/// Rechecks current tenant/subscription, principal/request/native TLS validity, registry/profile,
/// revocation, policy and witness/allocation evidence. It must never open another transaction,
/// await, block an async provider, create a grant or debit a second ledger. Default is deny.
/// </summary>
public interface IHrReadCurrentGuard
{
    bool Allows(FabricDatabase database, HrReadFabricAdmission admission, CancellationToken cancellationToken);
}

public sealed class UnavailableHrReadCurrentGuard : IHrReadCurrentGuard
{
    public bool Allows(FabricDatabase database, HrReadFabricAdmission admission, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return false; }
}

/// <summary>
/// Uses the existing Fabric schema and replay namespace. No independent authority store exists here.
/// The passed Fabric instance must be the host's shared database. All transaction/snapshot work is synchronous.
/// </summary>
public sealed class HrReadFabricAdapter : IHrReadFabricAdapter
{
    public const string ReplayBucket = "contract-policy.gateway-context.replay.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private readonly FabricDatabase database;
    private readonly IHrReadCurrentGuard guard;
    private readonly TimeProvider time;
    public HrReadFabricAdapter(FabricDatabase database, IHrReadCurrentGuard? guard = null, TimeProvider? time = null)
    { this.database = database ?? throw new ArgumentNullException(nameof(database)); this.guard = guard ?? new UnavailableHrReadCurrentGuard(); this.time = time ?? TimeProvider.System; }

    public bool TryClaimNonceInTransaction(HrReadAdmission admission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (admission is not HrReadFabricAdmission candidate || database.IsInTransaction) return false;
        var claimed = database.Transaction(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Current(candidate, cancellationToken)) return false;
            var key = candidate.Tier + ":" + candidate.RequestId;
            // BEGIN IMMEDIATE in the existing Fabric serializes independent claimers.
            // Any existing row, including corrupt evidence, is already consumed.
            if (database.ReadDocument(ReplayBucket, key) is not null) return false;
            database.Put(ReplayBucket, key, new ReplayReceipt(candidate.Tier, candidate.RequestId, candidate.KeyId, candidate.ExpiresAt));
            // A veto/cancellation after Put returns false WITHOUT throwing, so the nonce remains committed.
            try { return !cancellationToken.IsCancellationRequested && Current(candidate, cancellationToken); }
            catch (Exception) { return false; } // Commit the consumed nonce even if the post-claim guard fails.
        });
        cancellationToken.ThrowIfCancellationRequested(); // Observe cancellation only after the claim transaction has committed.
        return claimed;
    }

    public MaterializedHrRead ReadInSnapshot(HrReadAdmission admission, CancellationToken cancellationToken)
    {
        if (admission is not HrReadFabricAdmission candidate) throw new UnauthorizedAccessException("Unsupported admission evidence.");
        cancellationToken.ThrowIfCancellationRequested();
        return database.ReadSnapshot(() =>
        {
            if (!Current(candidate, cancellationToken)) throw new UnauthorizedAccessException("Current read authority is unavailable.");
            var current = ReadState("organization", candidate.Target.TenantId.ToString("D"), candidate, null)
                ?? new StoredOrgState(candidate.Target.TenantId, 0, [], [], [], [], null);
            StoredOrgState? state = current;
            if (candidate.Target.Kind == HrReadEndpointKind.History)
                state = candidate.Target.Revision > current.Revision ? null : ReadState("organization.history",
                    candidate.Target.TenantId.ToString("D") + ":" + candidate.Target.Revision!.Value.ToString(CultureInfo.InvariantCulture), candidate, candidate.Target.Revision);
            // Match OrganizationStore.Read: missing current organization is empty revision0; history is missing.
            // Match ASP.NET Web JSON: PascalCase stored typed state is emitted as camelCase, including roles:null.
            var result = state is null ? MaterializedHrRead.NotFound()
                : MaterializedHrRead.Existing(JsonSerializer.SerializeToElement(state, WebJson));
            if (!Current(candidate, cancellationToken)) throw new UnauthorizedAccessException("Read authority changed.");
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        });
    }

    public bool RevalidateInNewSnapshot(HrReadAdmission admission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (admission is not HrReadFabricAdmission candidate || database.IsInTransaction) return false;
        return database.ReadSnapshot(() => Current(candidate, cancellationToken));
    }

    private bool Current(HrReadFabricAdmission admission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        if (now >= admission.ValidUntil || HrReadFabricAdmission.Epoch(now) >= admission.ExpiresAt
            || !guard.Allows(database, admission, cancellationToken)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        now = time.GetUtcNow();
        return now < admission.ValidUntil && HrReadFabricAdmission.Epoch(now) < admission.ExpiresAt;
    }

    private StoredOrgState? ReadState(string kind, string key, HrReadFabricAdmission admission, long? exactRevision)
    {
        // Existing ReadDocument allocates SQLite's string before it returns. This cap bounds parsing and
        // materialization; it is not a pre-allocation SQLite byte cap. See SOURCE-MANIFEST.md.
        var document = database.ReadDocument(kind, key);
        if (document is null) return null;
        if (document.Json.Length > admission.MaximumSourceDocumentBytes
            || StrictUtf8.GetByteCount(document.Json) > admission.MaximumSourceDocumentBytes)
            throw new InvalidDataException("Persisted organization exceeds the source document byte cap.");
        using var parsed = JsonDocument.Parse(document.Json, new() { MaxDepth = 32 });
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid organization state.");
        RejectDuplicateProperties(root);
        var state = root.Deserialize<StoredOrgState>(StoredJson) ?? throw new InvalidDataException("Invalid organization state.");
        if (state.TenantId != admission.Target.TenantId || state.Revision < 0
            || exactRevision.HasValue && state.Revision != exactRevision
            || state.Units is null || state.Positions is null || state.Appointments is null || state.Changes is null
            || state.Units.Any(item => item is null) || state.Positions.Any(item => item is null)
            || state.Appointments.Any(item => item is null) || state.Changes.Any(item => item is null)
            || state.Roles?.Any(item => item is null) == true)
            throw new InvalidDataException("Organization state does not match its read target.");
        return state;
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate organization state field.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private sealed record ReplayReceipt(string Tier, string RequestId, string KeyId, decimal ExpiresAt);
    // Shapes pinned to OrganizationFabric.cs. No domain execution, extra ledger or new policy store is copied.
    private sealed record StoredOrgUnit(Guid Id, string Name, string Code, Guid? ParentId, string Kind, bool Active);
    private sealed record StoredOrgRole(Guid Id, string Name, string Code);
    private sealed record StoredOrgPosition(Guid Id, Guid UnitId, string Name, string Code, int Capacity, bool Active, Guid? RoleId = null);
    private sealed record StoredOrgAppointment(Guid Id, Guid PositionId, string Subject, DateTimeOffset From, DateTimeOffset? To);
    private sealed record StoredOrgChange(Guid Id, long Revision, string Actor, string Reason, DateTimeOffset RecordedAt, string Operation);
    private sealed record StoredOrgState(Guid TenantId, long Revision, StoredOrgUnit[] Units, StoredOrgPosition[] Positions,
        StoredOrgAppointment[] Appointments, StoredOrgChange[] Changes, StoredOrgRole[]? Roles = null);
}
