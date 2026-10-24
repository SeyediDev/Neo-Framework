using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fanasa.ContractPolicy;

/// <summary>
/// A bounded validator for the package's documented JSON Schema subset, not a general
/// JSON Schema implementation. Schemas and local references are compiled once at startup.
/// </summary>
public sealed class ContractSchemaValidator
{
    private const double MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly Regex FilenamePattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\.json\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly FrozenSet<string> Keywords = new[]
    {
        "$schema", "$id", "$comment", "title", "description", "default", "examples",
        "deprecated", "readOnly", "writeOnly", "$defs", "$ref", "type", "properties",
        "required", "additionalProperties", "items", "contains", "minContains", "maxContains",
        "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern", "enum",
        "const", "minimum", "maximum", "exclusiveMinimum", "format", "oneOf", "allOf",
        "if", "then", "else", "not"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> JsonTypes = new[]
    {
        "null", "boolean", "object", "array", "number", "integer", "string"
    }.ToFrozenSet(StringComparer.Ordinal);
    private readonly FrozenDictionary<string, Schema> _schemas;

    public ContractSchemaValidator(string schemaDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaDirectory);
        var documents = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.GetFullPath(schemaDirectory), "*.json",
                     SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var filename = Path.GetFileName(path);
            RequireLocalFilename(filename);
            try
            {
                documents.Add(filename, ContractJson.Parse(File.ReadAllText(path)));
            }
            catch (JsonException error)
            {
                throw new InvalidDataException($"Invalid schema JSON in {filename}: {error.Message}", error);
            }
        }

        if (documents.Count == 0)
            throw new InvalidDataException("The schema directory contains no local JSON schemas.");
        var compiler = new Compiler(documents);
        _schemas = documents.ToFrozenDictionary(pair => pair.Key,
            pair => compiler.Compile(pair.Value, pair.Value, "$schema(" + pair.Key + ")", 0),
            StringComparer.Ordinal);
    }

    /// <summary>Validates a value without reading files or mutating shared state.</summary>
    public IReadOnlyList<string> Validate(string schemaFilename, JsonNode? value)
    {
        RequireLocalFilename(schemaFilename);
        if (!_schemas.TryGetValue(schemaFilename, out var schema))
            return Array.AsReadOnly(new[] { $"$schema: unknown local schema {schemaFilename}." });
        var errors = new List<string>();
        var nodes = 0;
        Value? snapshot;
        try { snapshot = Snapshot(value, "$", 0, ref nodes, errors); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
                                      NotSupportedException or FormatException or OverflowException)
        {
            errors.Add("$: value must contain only supported JSON values.");
            snapshot = null;
        }
        if (errors.Count == 0)
        {
            var evaluation = new Evaluation();
            Evaluate(schema, snapshot!, "$", errors, 0, evaluation);
            if (evaluation.Exceeded)
                errors.Add("$: schema evaluation exceeds the contract policy validator limits.");
        }
        return Array.AsReadOnly(errors.ToArray());
    }

    private static void RequireLocalFilename(string filename)
    {
        if (filename is null || !FilenamePattern.IsMatch(filename) ||
            filename.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Schema filename must be a local .json filename without directories.",
                nameof(filename));
    }

    private static Value? Snapshot(JsonNode? node, string location, int depth, ref int nodes,
        List<string> errors)
    {
        if (depth > ContractJson.MaximumDepth || ++nodes > ContractJson.MaximumNodes)
        {
            errors.Add(location + ": value exceeds the contract policy validator limits.");
            return null;
        }
        if (node is null) return Value.Null;
        if (node is JsonObject obj)
        {
            var properties = new Dictionary<string, Value>(StringComparer.Ordinal);
            foreach (var pair in obj)
            {
                var child = Snapshot(pair.Value, PropertyLocation(location, pair.Key), depth + 1, ref nodes, errors);
                if (child is not null) properties.Add(pair.Key, child);
                if (nodes > ContractJson.MaximumNodes) break;
            }
            return new Value(JsonValueKind.Object) { Properties = properties.ToFrozenDictionary(StringComparer.Ordinal) };
        }
        if (node is JsonArray array)
        {
            var items = new List<Value>();
            for (var index = 0; index < array.Count; index++)
            {
                var child = Snapshot(array[index], $"{location}[{index}]", depth + 1, ref nodes, errors);
                if (child is not null) items.Add(child);
                if (nodes > ContractJson.MaximumNodes) break;
            }
            return new Value(JsonValueKind.Array) { Items = items.ToArray() };
        }
        try
        {
            var scalar = JsonSerializer.SerializeToElement(node, ContractJson.Options);
            switch (scalar.ValueKind)
            {
                case JsonValueKind.Null: return Value.Null;
                case JsonValueKind.True: return new Value(JsonValueKind.True);
                case JsonValueKind.False: return new Value(JsonValueKind.False);
                case JsonValueKind.String: return new Value(JsonValueKind.String) { Text = scalar.GetString()! };
                case JsonValueKind.Number:
                    var number = scalar.GetDouble();
                    if (!IsSafeNumber(number))
                    {
                        errors.Add(location + ": numbers must be finite and integers must be safe.");
                        return null;
                    }
                    return new Value(JsonValueKind.Number) { Number = number };
                default:
                    errors.Add(location + ": value must contain only JSON values.");
                    return null;
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
                                      NotSupportedException or FormatException or OverflowException)
        {
            errors.Add(location + ": value must contain only finite, safe JSON values.");
            return null;
        }
    }

    private static bool IsSafeNumber(double number) => double.IsFinite(number) &&
        (Math.Truncate(number) != number || Math.Abs(number) <= MaximumSafeInteger);

    private static string PropertyLocation(string location, string property) =>
        location + "[" + JsonSerializer.Serialize(property) + "]";

    private static string Canonical(Value value)
    {
        return value.Kind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => JsonSerializer.Serialize(value.Text),
            JsonValueKind.Number => value.Number == 0 ? "0" :
                value.Number.ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.Array => "[" + string.Join(',', value.Items!.Select(Canonical)) + "]",
            JsonValueKind.Object => "{" + string.Join(',', value.Properties!.OrderBy(pair => pair.Key,
                StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
            _ => throw new InvalidOperationException("Unsupported JSON value.")
        };
    }

    private static bool TypeMatches(string type, Value value) => type switch
    {
        "null" => value.Kind == JsonValueKind.Null,
        "boolean" => value.Kind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.Kind == JsonValueKind.Object,
        "array" => value.Kind == JsonValueKind.Array,
        "string" => value.Kind == JsonValueKind.String,
        "number" => value.Kind == JsonValueKind.Number,
        "integer" => value.Kind == JsonValueKind.Number && Math.Truncate(value.Number) == value.Number,
        _ => false
    };

    private static void Evaluate(Schema schema, Value value, string location, List<string> errors,
        int depth, Evaluation evaluation)
    {
        if (depth > ContractJson.MaximumDepth || ++evaluation.Nodes > ContractJson.MaximumNodes)
        {
            evaluation.Exceeded = true;
            errors.Add(location + ": schema evaluation exceeds the contract policy validator limits.");
            return;
        }
        void Add(string message) => errors.Add(location + ": " + message);
        bool Matches(Schema child)
        {
            var branchErrors = new List<string>();
            Evaluate(child, value, location, branchErrors, depth + 1, evaluation);
            return branchErrors.Count == 0 && !evaluation.Exceeded;
        }
        if (schema.Boolean is true) return;
        if (schema.Boolean is false) { Add("value is forbidden by the schema."); return; }
        if (schema.Reference is not null)
            Evaluate(schema.Reference, value, location, errors, depth + 1, evaluation);
        if (schema.Types is not null && !schema.Types.Any(type => TypeMatches(type, value)))
            Add("must have type " + string.Join(" or ", schema.Types) + ".");
        if (schema.Constant is not null && Canonical(value) != schema.Constant) Add("must equal const.");
        if (schema.Enum is not null && !schema.Enum.Contains(Canonical(value))) Add("must match an enum value.");
        if (value.Kind == JsonValueKind.String)
        {
            var length = value.Text!.EnumerateRunes().Count();
            if (schema.MinLength is { } min && length < min) Add($"must have at least {min} characters.");
            if (schema.MaxLength is { } max && length > max) Add($"must have at most {max} characters.");
            if (schema.Pattern is not null)
            {
                try
                {
                    if (!schema.Pattern.IsMatch(value.Text)) Add("must match pattern " +
                        JsonSerializer.Serialize(schema.PatternText) + ".");
                }
                catch (RegexMatchTimeoutException) { Add("pattern evaluation exceeded its time limit."); }
            }
            if (schema.DateTime && !ContractJson.IsCanonicalUtc(value.Text))
                Add("must be a real canonical UTC date-time with milliseconds.");
        }
        if (value.Kind == JsonValueKind.Number)
        {
            if (schema.Minimum is { } min && value.Number < min) Add($"must be at least {min.ToString("R", CultureInfo.InvariantCulture)}.");
            if (schema.Maximum is { } max && value.Number > max) Add($"must be at most {max.ToString("R", CultureInfo.InvariantCulture)}.");
            if (schema.ExclusiveMinimum is { } exclusive && value.Number <= exclusive)
                Add($"must be greater than {exclusive.ToString("R", CultureInfo.InvariantCulture)}.");
        }
        if (value.Kind == JsonValueKind.Array)
        {
            var items = value.Items!;
            if (schema.MinItems is { } min && items.Length < min) Add($"must have at least {min} items.");
            if (schema.MaxItems is { } max && items.Length > max) Add($"must have at most {max} items.");
            if (schema.UniqueItems && items.Select(Canonical).Distinct(StringComparer.Ordinal).Count() != items.Length)
                Add("must contain unique items.");
            for (var index = 0; schema.Items is not null && index < items.Length && !evaluation.Exceeded; index++)
                Evaluate(schema.Items, items[index], $"{location}[{index}]", errors, depth + 1, evaluation);
            if (schema.Contains is not null)
            {
                var count = 0;
                for (var index = 0; index < items.Length && !evaluation.Exceeded; index++)
                {
                    var branchErrors = new List<string>();
                    Evaluate(schema.Contains, items[index], $"{location}[{index}]", branchErrors, depth + 1, evaluation);
                    if (branchErrors.Count == 0 && !evaluation.Exceeded) count++;
                }
                var minimum = schema.MinContains ?? 1;
                if (count < minimum) Add($"must contain at least {minimum} matching items; matched {count}.");
                if (schema.MaxContains is { } maximum && count > maximum)
                    Add($"must contain at most {maximum} matching items; matched {count}.");
            }
        }
        if (value.Kind == JsonValueKind.Object)
        {
            var properties = value.Properties!;
            foreach (var required in schema.Required)
                if (!properties.ContainsKey(required)) Add("missing required property " + JsonSerializer.Serialize(required) + ".");
            foreach (var pair in properties)
            {
                if (evaluation.Exceeded) break;
                if (schema.Properties.TryGetValue(pair.Key, out var child))
                    Evaluate(child, pair.Value, PropertyLocation(location, pair.Key), errors, depth + 1, evaluation);
                else if (!schema.AdditionalProperties) Add("unknown property " + JsonSerializer.Serialize(pair.Key) + ".");
            }
        }
        foreach (var child in schema.AllOf)
        {
            if (evaluation.Exceeded) break;
            Evaluate(child, value, location, errors, depth + 1, evaluation);
        }
        if (schema.OneOf.Length > 0)
        {
            var count = 0;
            foreach (var child in schema.OneOf)
            {
                if (evaluation.Exceeded) break;
                if (Matches(child)) count++;
            }
            if (count != 1) Add($"must match exactly one oneOf branch; matched {count}.");
        }
        if (schema.Not is not null && !evaluation.Exceeded && Matches(schema.Not)) Add("must not match the not schema.");
        if (schema.If is not null && !evaluation.Exceeded)
        {
            var branch = Matches(schema.If) ? schema.Then : schema.Else;
            if (branch is not null && !evaluation.Exceeded)
                Evaluate(branch, value, location, errors, depth + 1, evaluation);
        }
    }

    private sealed class Compiler(Dictionary<string, JsonNode?> documents)
    {
        private readonly Dictionary<JsonNode, Schema> _compiled = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<JsonNode> _active = new(ReferenceEqualityComparer.Instance);
        private int _nodes;
        private int _constantNodes;

        public Schema Compile(JsonNode? node, JsonNode? root, string location, int depth)
        {
            if (depth > ContractJson.MaximumDepth || ++_nodes > ContractJson.MaximumNodes)
                throw Error(location, "schema exceeds the contract policy validator limits.");
            if (node is JsonValue boolean && boolean.TryGetValue<bool>(out var allowed))
                return new Schema { Boolean = allowed };
            if (node is not JsonObject obj) throw Error(location, "schema must be an object or boolean.");
            if (_active.Contains(node)) throw Error(location, "recursive schemas are unsupported.");
            if (_compiled.TryGetValue(node, out var previous)) return previous;
            _active.Add(node);
            foreach (var pair in obj)
                if (!Keywords.Contains(pair.Key)) throw Error(location, "unsupported schema keyword " + JsonSerializer.Serialize(pair.Key) + ".");
            var types = ReadStrings(obj, "type", location, allowSingle: true);
            if (types is not null && (types.Length == 0 || types.Any(type => !JsonTypes.Contains(type))))
                throw Error(location, "type must contain supported, distinct JSON types.");
            var required = ReadStrings(obj, "required", location) ?? [];
            var properties = ReadChildren(obj, "properties", root, location, depth);
            _ = ReadChildren(obj, "$defs", root, location, depth);
            Regex? pattern = null;
            string? patternText = null;
            if (obj.ContainsKey("pattern"))
            {
                var text = ReadString(obj["pattern"], location + "/pattern");
                patternText = text;
                try { pattern = new Regex(StrictAnchoredPattern(text), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException error) { throw Error(location, "pattern must be a valid regular expression.", error); }
            }
            var enumValues = ReadEnum(obj, location);
            var constant = obj.ContainsKey("const") ? ReadConstant(obj["const"], location + "/const") : null;
            var format = obj.ContainsKey("format") ? ReadString(obj["format"], location + "/format") : null;
            if (format is not null && format != "date-time") throw Error(location, "format only supports date-time.");
            Schema? reference = null;
            if (obj.ContainsKey("$ref"))
            {
                var text = ReadString(obj["$ref"], location + "/$ref");
                var target = Resolve(text, root, location);
                reference = Compile(target.Node, target.Root, location + "/$ref(" + text + ")", depth + 1);
            }
            var schema = new Schema
            {
                Types = types,
                Required = required,
                Properties = properties,
                AdditionalProperties = ReadBoolean(obj, "additionalProperties", location) ?? true,
                UniqueItems = ReadBoolean(obj, "uniqueItems", location) ?? false,
                MinItems = ReadCount(obj, "minItems", location),
                MaxItems = ReadCount(obj, "maxItems", location),
                MinContains = ReadCount(obj, "minContains", location),
                MaxContains = ReadCount(obj, "maxContains", location),
                MinLength = ReadCount(obj, "minLength", location),
                MaxLength = ReadCount(obj, "maxLength", location),
                Minimum = ReadNumber(obj, "minimum", location),
                Maximum = ReadNumber(obj, "maximum", location),
                ExclusiveMinimum = ReadNumber(obj, "exclusiveMinimum", location),
                Pattern = pattern,
                PatternText = patternText,
                DateTime = format is not null,
                Constant = constant,
                Enum = enumValues,
                Reference = reference,
                Items = ReadChild(obj, "items", root, location, depth),
                Contains = ReadChild(obj, "contains", root, location, depth),
                If = ReadChild(obj, "if", root, location, depth),
                Then = ReadChild(obj, "then", root, location, depth),
                Else = ReadChild(obj, "else", root, location, depth),
                Not = ReadChild(obj, "not", root, location, depth),
                AllOf = ReadBranches(obj, "allOf", root, location, depth),
                OneOf = ReadBranches(obj, "oneOf", root, location, depth)
            };
            _active.Remove(node);
            _compiled.Add(node, schema);
            return schema;
        }

        private (JsonNode? Node, JsonNode? Root) Resolve(string reference, JsonNode? root, string location)
        {
            var parts = reference.Split('#');
            if (parts.Length > 2 || reference.Length == 0)
                throw Error(location, "$ref must name a local schema or a local $defs pointer.");
            if (parts[0].Length > 0)
            {
                try { RequireLocalFilename(parts[0]); }
                catch (ArgumentException error) { throw Error(location, error.Message, error); }
                if (!documents.TryGetValue(parts[0], out root)) throw Error(location, "unresolved local $ref " + reference + ".");
            }
            if (parts.Length == 1) return (root, root);
            var fragment = parts[1];
            if (!fragment.StartsWith("/$defs/", StringComparison.Ordinal) || fragment.Length == 7 || fragment.Contains('%'))
                throw Error(location, "$ref fragments must be local #/$defs/... JSON pointers.");
            var target = root;
            foreach (var token in fragment[1..].Split('/'))
            {
                for (var index = 0; index < token.Length; index++)
                    if (token[index] == '~' && (index + 1 >= token.Length || token[index + 1] is not ('0' or '1')))
                        throw Error(location, "$ref contains an invalid JSON pointer escape.");
                    else if (token[index] == '~') index++;
                var key = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (target is not JsonObject parent || !parent.TryGetPropertyValue(key, out target))
                    throw Error(location, "unresolved local $ref " + reference + ".");
            }
            return (target, root);
        }

        private FrozenDictionary<string, Schema> ReadChildren(JsonObject obj, string keyword, JsonNode? root,
            string location, int depth)
        {
            if (!obj.ContainsKey(keyword)) return FrozenDictionary<string, Schema>.Empty;
            if (obj[keyword] is not JsonObject children) throw Error(location, keyword + " must be an object of schemas.");
            return children.ToFrozenDictionary(pair => pair.Key,
                pair => Compile(pair.Value, root, location + "/" + keyword + "/" + pair.Key, depth + 1), StringComparer.Ordinal);
        }

        private Schema? ReadChild(JsonObject obj, string keyword, JsonNode? root, string location, int depth) =>
            obj.ContainsKey(keyword) ? Compile(obj[keyword], root, location + "/" + keyword, depth + 1) : null;

        private Schema[] ReadBranches(JsonObject obj, string keyword, JsonNode? root, string location, int depth)
        {
            if (!obj.ContainsKey(keyword)) return [];
            if (obj[keyword] is not JsonArray branches || branches.Count == 0)
                throw Error(location, keyword + " must be a nonempty array of schemas.");
            return branches.Select((branch, index) => Compile(branch, root,
                location + "/" + keyword + "/" + index, depth + 1)).ToArray();
        }

        private FrozenSet<string>? ReadEnum(JsonObject obj, string location)
        {
            if (!obj.ContainsKey("enum")) return null;
            if (obj["enum"] is not JsonArray values || values.Count == 0)
                throw Error(location, "enum must be a nonempty array.");
            var canonical = values.Select((value, index) => ReadConstant(value, location + "/enum/" + index)).ToArray();
            var set = canonical.ToFrozenSet(StringComparer.Ordinal);
            if (set.Count != canonical.Length) throw Error(location, "enum values must be distinct.");
            return set;
        }

        private string ReadConstant(JsonNode? node, string location)
        {
            var errors = new List<string>();
            var snapshot = Snapshot(node, location, 0, ref _constantNodes, errors);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            return Canonical(snapshot!);
        }

        private static string[]? ReadStrings(JsonObject obj, string keyword, string location, bool allowSingle = false)
        {
            if (!obj.ContainsKey(keyword)) return null;
            if (allowSingle && obj[keyword] is JsonValue single && single.TryGetValue<string>(out var text)) return [text];
            if (obj[keyword] is not JsonArray values) throw Error(location, keyword + " must be an array of distinct strings.");
            var result = values.Select(value => ReadString(value, location + "/" + keyword)).ToArray();
            if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
                throw Error(location, keyword + " must contain distinct strings.");
            return result;
        }

        private static string ReadString(JsonNode? node, string location) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text :
                throw Error(location, "must be a string.");

        private static string StrictAnchoredPattern(string pattern)
        {
            // The package's identifier patterns begin with ^ and end with $.
            // .NET's $ also accepts a terminal newline; the package requires the
            // entire identifier. Leave general substring patterns and literal $
            // expressions unchanged.
            if (!pattern.StartsWith('^') || !pattern.EndsWith('$')) return pattern;
            var escapes = 0;
            for (var index = pattern.Length - 2; index >= 0 && pattern[index] == '\\'; index--) escapes++;
            return escapes % 2 == 0 ? pattern[..^1] + @"\z" : pattern;
        }

        private static bool? ReadBoolean(JsonObject obj, string keyword, string location)
        {
            if (!obj.ContainsKey(keyword)) return null;
            return obj[keyword] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag :
                throw Error(location, keyword + " must be boolean.");
        }

        private static double? ReadNumber(JsonObject obj, string keyword, string location)
        {
            if (!obj.ContainsKey(keyword)) return null;
            if (obj[keyword] is not JsonValue value || !value.TryGetValue<double>(out var number) || !IsSafeNumber(number))
                throw Error(location, keyword + " must be a finite JSON number with safe integer precision.");
            return number;
        }

        private static double? ReadCount(JsonObject obj, string keyword, string location)
        {
            var number = ReadNumber(obj, keyword, location);
            if (number is { } count && (count < 0 || Math.Truncate(count) != count))
                throw Error(location, keyword + " must be a nonnegative safe integer.");
            return number;
        }

        private static InvalidDataException Error(string location, string message, Exception? inner = null) =>
            new(location + ": " + message, inner);
    }

    private sealed class Evaluation
    {
        public int Nodes;
        public bool Exceeded;
    }

    private sealed class Value(JsonValueKind kind)
    {
        public static readonly Value Null = new(JsonValueKind.Null);
        public JsonValueKind Kind { get; } = kind;
        public string? Text { get; init; }
        public double Number { get; init; }
        public FrozenDictionary<string, Value>? Properties { get; init; }
        public Value[]? Items { get; init; }
    }

    private sealed class Schema
    {
        public bool? Boolean { get; init; }
        public string[]? Types { get; init; }
        public string[] Required { get; init; } = [];
        public FrozenDictionary<string, Schema> Properties { get; init; } = FrozenDictionary<string, Schema>.Empty;
        public bool AdditionalProperties { get; init; } = true;
        public bool UniqueItems { get; init; }
        public double? MinItems { get; init; }
        public double? MaxItems { get; init; }
        public double? MinContains { get; init; }
        public double? MaxContains { get; init; }
        public double? MinLength { get; init; }
        public double? MaxLength { get; init; }
        public double? Minimum { get; init; }
        public double? Maximum { get; init; }
        public double? ExclusiveMinimum { get; init; }
        public Regex? Pattern { get; init; }
        public string? PatternText { get; init; }
        public bool DateTime { get; init; }
        public string? Constant { get; init; }
        public FrozenSet<string>? Enum { get; init; }
        public Schema? Reference { get; init; }
        public Schema? Items { get; init; }
        public Schema? Contains { get; init; }
        public Schema? If { get; init; }
        public Schema? Then { get; init; }
        public Schema? Else { get; init; }
        public Schema? Not { get; init; }
        public Schema[] AllOf { get; init; } = [];
        public Schema[] OneOf { get; init; } = [];
    }
}
