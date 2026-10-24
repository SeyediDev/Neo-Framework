using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fanasa.AccessManagement.Web.Accounting;

// Financial input must not lose digits before domain validation receives it.
public sealed class ExactBillingDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string raw;
        if (reader.TokenType == JsonTokenType.String) raw = reader.GetString() ?? throw new JsonException();
        else if (reader.TokenType == JsonTokenType.Number)
        {
            using var numeric = JsonDocument.ParseValue(ref reader);
            raw = numeric.RootElement.GetRawText();
        }
        else throw new JsonException("An exact decimal number or string is required.");
        if (raw.Length is 0 or > 64 || raw != raw.Trim()) throw new JsonException("Invalid exact decimal.");
        try
        {
            using var document = BillingBalance.StrictJson(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Number) throw new JsonException("Invalid exact decimal.");
            return document.RootElement.GetDecimal();
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or FormatException)
        { throw new JsonException("The financial number cannot be represented exactly.", error); }
    }
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
