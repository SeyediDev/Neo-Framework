using System.Globalization;
using System.Text.RegularExpressions;
namespace Fanasa.AccessManagement.Web.Governance;
public sealed record PipAttributeDefinition(string Name, string DataType, bool Required = true, string? ExternalName = null);
public static class PipSchema
{
    private static readonly string[] UtcDateTimeFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
    public static PipAttributeDefinition[] Default => [new("employment.active", "boolean"), new("organization.unit", "string", false)];
    public static PipAttributeDefinition[] Definitions(PipSourceDefinition source) => source.Definitions ?? Default;
    public static void Validate(PipAttributeDefinition[] definitions)
    {
        if (definitions.Length is < 1 or > 100) throw new ArgumentException("PIP schema must define 1 to 100 attributes.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var external = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in definitions)
            if (field is null || !ValidName(field.Name) || !ValidName(field.ExternalName ?? field.Name)
                || !names.Add(field.Name) || !external.Add(field.ExternalName ?? field.Name)
                || field.DataType is not ("string" or "boolean" or "integer" or "decimal" or "datetime"))
                throw new ArgumentException("PIP schema has invalid or duplicate names/types.");
    }
    private static bool ValidName(string? name) => name is { Length: <= 100 }
        && Regex.IsMatch(name, "^[a-zA-Z][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)
        && !name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("token", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("password", StringComparison.OrdinalIgnoreCase);
    public static void ValidateAttributes(PipSourceDefinition source, IReadOnlyDictionary<string, string> attributes)
    {
        var definitions = Definitions(source); Validate(definitions);
        if (attributes is null || attributes.Count > definitions.Length) throw new InvalidDataException("PIP attributes violate the schema.");
        foreach (var pair in attributes)
        {
            var definition = definitions.SingleOrDefault(x => x.Name == pair.Key) ?? throw new InvalidDataException("Undeclared PIP attribute.");
            ValidateValue(definition, pair.Value);
        }
        if (definitions.Any(x => x.Required && !attributes.ContainsKey(x.Name))) throw new InvalidDataException("Required PIP attribute is missing.");
    }
    public static void ValidateValue(PipAttributeDefinition definition, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000 || value.Any(char.IsControl)) throw new InvalidDataException("Invalid PIP attribute value.");
        var valid = definition.DataType switch
        {
            "string" => true,
            "boolean" => value is "true" or "false",
            "integer" => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "decimal" => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
            "datetime" => TryParseUtcDateTime(value, out _),
            _ => false
        };
        if (!valid) throw new InvalidDataException("PIP attribute value does not match its declared type.");
    }

    public static bool ValuesEqual(PipAttributeDefinition definition, string left, string right)
    {
        ValidateValue(definition, left); ValidateValue(definition, right);
        return definition.DataType switch
        {
            "integer" => long.Parse(left, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                == long.Parse(right, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            "decimal" => decimal.Parse(left, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
                == decimal.Parse(right, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            "datetime" => ParseUtcDateTime(left) == ParseUtcDateTime(right),
            _ => string.Equals(left, right, StringComparison.Ordinal)
        };
    }

    private static bool TryParseUtcDateTime(string value, out DateTimeOffset at)
    {
        at = default;
        return Regex.IsMatch(value, "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:Z|\\+00:00)$", RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParseExact(value, UtcDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at)
            && at.Offset == TimeSpan.Zero;
    }

    private static DateTimeOffset ParseUtcDateTime(string value) => TryParseUtcDateTime(value, out var at)
        ? at : throw new InvalidDataException("PIP datetime values require a full ISO 8601 date and explicit UTC timezone.");
}
