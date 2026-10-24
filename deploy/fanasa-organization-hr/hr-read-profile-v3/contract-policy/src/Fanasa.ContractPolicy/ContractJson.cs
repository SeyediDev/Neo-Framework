using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fanasa.ContractPolicy;

/// <summary>Strict JSON boundaries for the contract policy implementation.</summary>
public static class ContractJson
{
    public const int MaximumDepth = 64;
    public const int MaximumNodes = 10_000;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;

    /// <summary>Shared, read-only serializer settings; property names are case sensitive.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonNode? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumDepth
        });
        var objects = new Stack<HashSet<string>>();
        var nodes = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var property = reader.GetString()!;
                    if (!objects.Peek().Add(property))
                        throw new JsonException($"Duplicate JSON property {JsonSerializer.Serialize(property)}.");
                    break;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray or
                JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or
                JsonTokenType.False or JsonTokenType.Null)
            {
                if (++nodes > MaximumNodes)
                    throw new JsonException("JSON exceeds the contract policy node limit.");
            }
        }

        return JsonNode.Parse(bytes, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
    }

    public static JsonNode? ToNode<T>(T value) =>
        Parse(JsonSerializer.Serialize(value, Options));

    public static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    // Raw JsonNode callers may use another parser, including JsonNode's lazy
    // duplicate-property representation. Normalize through this strict boundary
    // before reading properties or deserializing DTOs.
    internal static bool TrySnapshot(JsonNode? value, out JsonNode? snapshot)
    {
        snapshot = null;
        if (value is null) return true;
        try
        {
            snapshot = Parse(value.ToJsonString(Options));
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
                                      NotSupportedException or FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Accepts only a real UTC instant with exactly three millisecond digits.</summary>
    public static bool IsCanonicalUtc(string? text)
    {
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        return text is { Length: 24 } &&
            DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant) &&
            string.Equals(text, instant.ToString(format, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            RespectRequiredConstructorParameters = true,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = MaximumDepth
        };
        // JSON Schema integers are numerical values, so 7, 7.0 and 7e0 have the
        // same meaning. The nullable converter automatically wraps this converter.
        options.Converters.Add(new SafeIntegerConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class SafeIntegerConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var number) ||
                !double.IsFinite(number) || Math.Truncate(number) != number ||
                Math.Abs(number) > MaximumSafeInteger)
                throw new JsonException("Expected a safe JSON integer.");
            return checked((long)number);
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        {
            if (value < -MaximumSafeInteger || value > MaximumSafeInteger)
                throw new JsonException("JSON integers must have safe integer precision.");
            writer.WriteNumberValue(value);
        }
    }
}
