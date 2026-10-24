using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

public sealed record GatewayPublicationSnapshot(string EventId, long AggregateVersion,
    string EventFingerprint, ContractAccessProjection Projection, string LimitsSha256, string RecordedAt)
{
    public string ProducerClientId { get; init; } = "";
}
public sealed record GatewayObservedConfiguration(long ContractVersion, string AllocationId,
    string CanonicalScope, long ConfigRevision, string LimitsSha256, string ObservedAt);
public sealed record GatewayProjectionAcknowledgement(string EventId, long AggregateVersion,
    string Phase, GatewayObservedConfiguration Gateway);

/// <summary>A trusted host adapter must authenticate the Gateway and observe this exact
/// publication. Projection acceptance, a provisioning response or an admission context
/// cannot establish a rollout observation. There is currently no native observation API.</summary>
public interface IContractGatewayObserver
{
    bool IsConfigured { get; }
    ValueTask<GatewayObservedConfiguration?> ObserveAsync(GatewayPublicationSnapshot publication,
        CancellationToken cancellationToken);
}
internal sealed class UnconfiguredContractGatewayObserver : IContractGatewayObserver
{
    public bool IsConfigured => false;
    public ValueTask<GatewayObservedConfiguration?> ObserveAsync(GatewayPublicationSnapshot publication,
        CancellationToken cancellationToken) => ValueTask.FromResult<GatewayObservedConfiguration?>(null);
}

public sealed class ContractGatewayPublicationService(ContractPolicyStore store, IContractGatewayObserver observer)
{
    public async ValueTask<GatewayProjectionAcknowledgement?> ObserveAsync(string contractId,
        string eventId, Func<bool> authorize, CancellationToken cancellationToken)
    {
        if (!authorize()) return null;
        var publication = store.ReadGatewayPublication(contractId, eventId);
        if (publication is null) return null;
        var existing = store.ReadGatewayAcknowledgement(contractId, eventId);
        if (existing is not null) return authorize() ? existing : null;
        if (!observer.IsConfigured) return null;
        try
        {
            // Await outside Fabric's synchronous transaction. The commit rechecks
            // current authority, the exact stored snapshot and the active version.
            var observation = await observer.ObserveAsync(ContractJson.Copy(publication), cancellationToken);
            return observation is null ? null : store.RecordGatewayObservation(contractId, eventId, observation, authorize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}

public sealed partial class ContractPolicyStore
{
    private const string GatewayPublicationKind = "contract-policy.gateway-publication";
    private const string GatewayObservationKind = "contract-policy.gateway-observation";

    private void CaptureGatewayPublication(ContractProjectionEvent value, string producerClientId, string fingerprint)
    {
        var key = GatewayKey(value.Projection.ContractId, value.EventId);
        var snapshot = new GatewayPublicationSnapshot(value.EventId, value.AggregateVersion, fingerprint,
            ContractJson.Copy(value.Projection), GatewayCanonicalJson.Hash(value.Projection.Limits),
            FabricOrganizationAccessSnapshotSource.Format(time.GetUtcNow())) { ProducerClientId = producerClientId };
        var previous = ReadGatewayPublication(value.Projection.ContractId, value.EventId);
        if (previous is not null)
        {
            if (!Equivalent(previous with { RecordedAt = snapshot.RecordedAt }, snapshot))
                throw new InvalidDataException("Gateway publication input changed.");
            return;
        }
        database.Put(GatewayPublicationKind, key, snapshot);
        database.Emit("contract-gateway-publication:" + key, "ContractGatewayPublicationPending",
            snapshot, time.GetUtcNow());
    }

    public GatewayPublicationSnapshot? ReadGatewayPublication(string contractId, string eventId)
    {
        var value = database.Read<GatewayPublicationSnapshot>(GatewayPublicationKind, GatewayKey(contractId, eventId));
        if (value is null) return null;
        if (value.EventId != eventId || value.Projection is null || value.Projection.ContractId != contractId
            || value.AggregateVersion != value.Projection.ContractVersion || !Hash.IsMatch(value.EventFingerprint)
            || !ContractJson.IsCanonicalUtc(value.RecordedAt) || string.IsNullOrWhiteSpace(value.ProducerClientId)
            || module.Projections.Validate(value.Projection).Count != 0
            || value.LimitsSha256 != GatewayCanonicalJson.Hash(value.Projection.Limits))
            throw new InvalidDataException("Invalid Gateway publication snapshot.");
        return ContractJson.Copy(value);
    }

    public GatewayProjectionAcknowledgement? ReadGatewayAcknowledgement(string contractId, string eventId)
    {
        var snapshot = ReadGatewayPublication(contractId, eventId);
        var value = database.Read<GatewayProjectionAcknowledgement>(GatewayObservationKind, GatewayKey(contractId, eventId));
        if (snapshot is null || value is null) return null;
        if (value.EventId != snapshot.EventId || value.AggregateVersion != snapshot.AggregateVersion
            || value.Phase != "acknowledged" || !Matches(snapshot, value.Gateway))
            throw new InvalidDataException("Invalid stored Gateway observation.");
        // Historical observations remain durable, but never acknowledge a
        // superseded input as if it described the current Gateway generation.
        return IsCurrent(snapshot) ? ContractJson.Copy(value) : null;
    }

    public GatewayProjectionAcknowledgement? RecordGatewayObservation(string contractId, string eventId,
        GatewayObservedConfiguration observation, Func<bool> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        if (!authorize()) return null;
        var owned = ContractJson.Copy(observation);
        try
        {
            return database.Transaction(() =>
            {
                if (!authorize()) return null;
                var snapshot = ReadGatewayPublication(contractId, eventId)
                    ?? throw new InvalidOperationException("No publication exists for this event.");
                if (!Matches(snapshot, owned) || !IsCurrent(snapshot)
                    || DateTimeOffset.Parse(owned.ObservedAt, CultureInfo.InvariantCulture) > time.GetUtcNow()
                    || time.GetUtcNow() >= DateTimeOffset.Parse(snapshot.Projection.ValidTo, CultureInfo.InvariantCulture))
                    throw new ArgumentException("Observation does not match the current immutable publication.");
                var existing = ReadGatewayAcknowledgement(contractId, eventId);
                if (existing is not null)
                {
                    if (!Equivalent(existing.Gateway, owned)) throw new ArgumentException("An observed receipt is immutable.");
                    return authorize() ? existing : null;
                }
                var ack = new GatewayProjectionAcknowledgement(eventId, snapshot.AggregateVersion, "acknowledged", owned);
                database.Put(GatewayObservationKind, GatewayKey(contractId, eventId), ack);
                database.Emit("contract-gateway-observed:" + GatewayKey(contractId, eventId),
                    "ContractGatewayObserved", ack, time.GetUtcNow());
                if (!authorize()) throw new GatewayAuthorityChanged();
                return ContractJson.Copy(ack);
            });
        }
        catch (GatewayAuthorityChanged) { return null; }
    }

    private bool IsCurrent(GatewayPublicationSnapshot snapshot) =>
        Read(snapshot.Projection.ContractId) is { } state && Equivalent(state.Projection, snapshot.Projection)
        && ReadAllocation(snapshot.Projection.Limits.AllocationId) is { } allocation
        && Equivalent(allocation, snapshot.Projection.Limits)
        && time.GetUtcNow() >= DateTimeOffset.Parse(snapshot.Projection.EffectiveAt, CultureInfo.InvariantCulture)
        && time.GetUtcNow() < DateTimeOffset.Parse(snapshot.Projection.ValidTo, CultureInfo.InvariantCulture);
    private static bool Matches(GatewayPublicationSnapshot snapshot, GatewayObservedConfiguration? observation) =>
        observation is not null && observation.ContractVersion == snapshot.Projection.ContractVersion
        && observation.AllocationId == snapshot.Projection.Limits.AllocationId
        && observation.CanonicalScope == snapshot.Projection.Limits.CanonicalScope
        && observation.ConfigRevision == snapshot.Projection.Limits.ConfigRevision
        && observation.LimitsSha256 == snapshot.LimitsSha256
        && ContractJson.IsCanonicalUtc(observation.ObservedAt)
        && string.CompareOrdinal(observation.ObservedAt, snapshot.Projection.EffectiveAt) >= 0;
    private static string GatewayKey(string contractId, string eventId)
    {
        RequireId(contractId, nameof(contractId)); RequireId(eventId, nameof(eventId));
        return contractId + ":" + eventId;
    }
    private sealed class GatewayAuthorityChanged : Exception;
}

/// <summary>NeoContract serviceLimitHash-compatible JSON. Object keys are sorted,
/// array order is preserved, and finite safe numbers use JSON.stringify placement.</summary>
public static class GatewayCanonicalJson
{
    public static string Hash<T>(T value)
    {
        var json = new StringBuilder();
        Write(json, ContractJson.ToNode(value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString())));
    }
    private static void Write(StringBuilder writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.Append('{');
                var separator = false;
                foreach (var item in obj.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    if (separator) writer.Append(','); separator = true;
                    Quote(writer, item.Key); writer.Append(':'); Write(writer, item.Value);
                }
                writer.Append('}'); break;
            case JsonArray array:
                writer.Append('[');
                for (var index = 0; index < array.Count; index++) { if (index > 0) writer.Append(','); Write(writer, array[index]); }
                writer.Append(']'); break;
            case null: writer.Append("null"); break;
            default:
                if (node.GetValueKind() == JsonValueKind.Number) writer.Append(Number(node.GetValue<double>()));
                else if (node.GetValueKind() == JsonValueKind.String) Quote(writer, node.GetValue<string>());
                else writer.Append(node.ToJsonString(ContractJson.Options));
                break;
        }
    }
    private static void Quote(StringBuilder writer, string value)
    {
        // JSON.stringify emits valid non-BMP characters as UTF-8, while the
        // .NET JSON encoder escapes them even in its relaxed mode.
        writer.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            switch (character)
            {
                case '"': writer.Append("\\\""); break;
                case '\\': writer.Append("\\\\"); break;
                case '\b': writer.Append("\\b"); break;
                case '\f': writer.Append("\\f"); break;
                case '\n': writer.Append("\\n"); break;
                case '\r': writer.Append("\\r"); break;
                case '\t': writer.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(character) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        writer.Append(character).Append(value[++i]);
                    else if (character < 0x20 || char.IsSurrogate(character))
                        writer.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else writer.Append(character);
                    break;
            }
        }
        writer.Append('"');
    }
    private static string Number(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Nonfinite JSON number.");
        if (value == 0) return "0";
        var sign = value < 0 ? "-" : "";
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = text.IndexOfAny(['e', 'E']);
        var exponent = e < 0 ? 0 : int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? text : text[..e];
        var point = mantissa.IndexOf('.');
        var position = (point < 0 ? mantissa.Length : point) + exponent;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var zeros = digits.TakeWhile(x => x == '0').Count(); digits = digits[zeros..]; position -= zeros;
        if (digits.Length <= position && position <= 21) return sign + digits + new string('0', position - digits.Length);
        if (position > 0 && position <= 21) return sign + digits[..position] + "." + digits[position..];
        if (position > -6 && position <= 0) return sign + "0." + new string('0', -position) + digits;
        var scientificExponent = position - 1;
        return sign + digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e"
            + (scientificExponent >= 0 ? "+" : "") + scientificExponent.ToString(CultureInfo.InvariantCulture);
    }
}
