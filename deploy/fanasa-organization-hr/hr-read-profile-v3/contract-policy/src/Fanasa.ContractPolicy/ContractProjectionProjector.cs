using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fanasa.ContractPolicy;

/// <summary>
/// Applies complete contract snapshots without changing an input or accessing storage.
/// The caller persists the returned projection and receipts atomically.
/// </summary>
public sealed class ContractProjectionProjector(ContractProjectionValidator validator) : IContractProjectionProjector
{
    private readonly ContractProjectionValidator _validator = validator
        ?? throw new ArgumentNullException(nameof(validator));

    public ProjectionResult Apply(
        ProjectionState? state,
        ContractProjectionEvent? evt,
        string? authenticatedProducer)
    {
        ProjectionResult Rejected(params string[] errors) => Result("rejected", state, errors);

        var errors = _validator.ValidateEvent(evt).ToList();
        if (errors.Count != 0)
            return Result("rejected", state, errors);

        if (authenticatedProducer != "fanasa-contracts" || evt!.Producer != authenticatedProducer)
            return Rejected("Untrusted producer");

        errors.AddRange(_validator.Validate(evt.Projection));
        if (evt.AggregateVersion != evt.Projection.ContractVersion)
            errors.Add("Aggregate/projection version mismatch");
        if (errors.Count != 0)
            return Result("rejected", state, errors);

        var fingerprint = Fingerprint(evt);
        var receipt = state?.Receipts.FirstOrDefault(x => x.EventId == evt.EventId);
        if (receipt is not null)
            return Result(receipt.Fingerprint == fingerprint ? "duplicate" : "conflict", state);

        var previous = state?.Projection;
        var next = evt.Projection;
        if (previous is not null)
        {
            if (previous.SchemaVersion != next.SchemaVersion)
                return Rejected("Explicit projection schema migration required");

            if (previous.ContractId != next.ContractId
                || previous.OwnerTenantId != next.OwnerTenantId
                || previous.Ownership != next.Ownership
                || previous.ConsumerTenantId != next.ConsumerTenantId
                || previous.ProductId != next.ProductId
                || previous.OfferId != next.OfferId
                || previous.SubscriptionId != next.SubscriptionId)
                return Rejected("Aggregate identity changed");

            if (previous.Limits.AllocationId != next.Limits.AllocationId
                || previous.Limits.CanonicalScope != next.Limits.CanonicalScope)
                return Rejected("Allocation identity changed; explicit reallocation is outside this sample");

            // An older complete snapshot cannot replace the current aggregate or
            // its allocation configuration. Retain its receipt even when its
            // independent configuration revision predates the current one.
            if (evt.AggregateVersion < previous.ContractVersion)
                return WithReceipt("stale", previous, state!.Receipts, evt.EventId, fingerprint);

            if (next.SchemaVersion is "2.0.0" or "3.0.0")
            {
                if (next.Limits.ConfigRevision < previous.Limits.ConfigRevision)
                    return Rejected("Stale allocation configRevision");
                if (next.Limits.ConfigRevision == previous.Limits.ConfigRevision
                    && CanonicalJson(next.Limits) != CanonicalJson(previous.Limits))
                    return Result("conflict", state, ["Equal allocation configRevision with different limits"]);
            }

            if (evt.AggregateVersion == previous.ContractVersion)
            {
                if (CanonicalJson(previous) != CanonicalJson(next))
                    return Result("conflict", state);
                return WithReceipt("duplicate", previous, state!.Receipts, evt.EventId, fingerprint);
            }

            if (previous.Status == "Ended" && next.Status != "Ended")
                return Rejected("Ended subscription cannot resume");
        }

        return WithReceipt("applied", next, state?.Receipts ?? [], evt.EventId, fingerprint);
    }

    private static ProjectionResult Result(
        string status,
        ProjectionState? state,
        IEnumerable<string>? errors = null) =>
        new(status, CopyState(state), errors?.ToArray() ?? []);

    private static ProjectionResult WithReceipt(
        string status,
        ContractAccessProjection projection,
        IReadOnlyList<EventReceipt> receipts,
        string eventId,
        string fingerprint) =>
        new(status,
            new ProjectionState(
                ContractJson.Copy(projection),
                receipts.Select(x => new EventReceipt(x.EventId, x.Fingerprint))
                    .Append(new EventReceipt(eventId, fingerprint)).ToArray()),
            []);

    private static ProjectionState? CopyState(ProjectionState? state) =>
        state is null ? null : new ProjectionState(
            ContractJson.Copy(state.Projection),
            state.Receipts.Select(x => new EventReceipt(x.EventId, x.Fingerprint)).ToArray());

    private static string Fingerprint(ContractProjectionEvent evt) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes(evt)));

    private static string CanonicalJson<T>(T value) =>
        System.Text.Encoding.UTF8.GetString(CanonicalBytes(value));

    private static byte[] CanonicalBytes<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteCanonical(writer, ContractJson.ToNode(value));
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                if (node.GetValueKind() == JsonValueKind.Number)
                    writer.WriteRawValue(CanonicalNumber(node.GetValue<double>()));
                else
                    node.WriteTo(writer, ContractJson.Options);
                break;
        }
    }

    private static string CanonicalNumber(double value)
    {
        // Match JSON.stringify's decimal/exponent placement, including rates
        // below one micro-request per second. Projection integers are limited
        // to JavaScript's safe integer range by the validator.
        if (value == 0)
            return "0";
        var sign = value < 0 ? "-" : "";
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOfAny(['e', 'E']);
        var exponent = exponentAt < 0 ? 0 : int.Parse(text[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var point = mantissa.IndexOf('.');
        var position = (point < 0 ? mantissa.Length : point) + exponent;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var leadingZeros = digits.TakeWhile(x => x == '0').Count();
        digits = digits[leadingZeros..];
        position -= leadingZeros;

        if (digits.Length <= position && position <= 21)
            return sign + digits + new string('0', position - digits.Length);
        if (position > 0 && position <= 21)
            return sign + digits[..position] + "." + digits[position..];
        if (position > -6 && position <= 0)
            return sign + "0." + new string('0', -position) + digits;

        var scientificExponent = position - 1;
        return sign + digits[..1]
            + (digits.Length > 1 ? "." + digits[1..] : "")
            + "e" + (scientificExponent >= 0 ? "+" : "")
            + scientificExponent.ToString(CultureInfo.InvariantCulture);
    }
}
