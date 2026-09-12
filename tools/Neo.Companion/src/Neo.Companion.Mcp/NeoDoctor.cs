using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neo.Companion.Mcp;

public sealed record DoctorEvidence(string Path, int Line, string Observation, string? JsonPointer = null);
public sealed record DoctorFinding(string Code, string Severity, string Certainty, string Subject,
    string Message, string Suggestion, IReadOnlyList<DoctorEvidence> Evidence);
public sealed record DoctorReport(string Mode, string Status, string BundledBaseline, string ConfigurationSection,
    int FilesRead, IReadOnlyList<string> SkippedPaths, IReadOnlyList<DoctorFinding> Findings,
    IReadOnlyList<string> Limitations);

/// <summary>Syntax-only diagnostics. Never loads assemblies or evaluates a user's project.</summary>
internal sealed class NeoDoctor(string root, string baseline, string section)
{
    private const int MaxEntries = 5000, MaxFileBytes = 512 * 1024, MaxTotalBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", ".agents", "node_modules", "artifacts", ".neo", ".local" };
    private readonly List<DoctorFinding> findings = [];
    private readonly List<string> skipped = [];
    private readonly List<Unit> units = [];
    private bool incomplete;
    private int filesRead, bytesRead, entries;

    internal DoctorReport Run()
    {
        if (section.Length > 100 || section.Split(':').Any(x => x.Length == 0) && section.Length != 0)
            throw new ArgumentException("Use a colon-separated configuration section, or an empty string for the JSON root.");
        RejectLinks(root);
        Scan(root, 0);
        AnalyzeCode();
        return new("csharp-syntax-and-json", incomplete ? "incomplete" : "review-required", baseline, section,
            filesRead, skipped.Order(StringComparer.Ordinal).ToArray(), findings.OrderBy(x => x.Evidence[0].Path, StringComparer.Ordinal)
                .ThenBy(x => x.Evidence[0].Line).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray(),
            ["Findings are review candidates, not proven runtime faults. No findings is not a health check.",
             "One consuming application per scan. All C# below this root is considered; project inclusions, conditions, call order and control flow are not evaluated.",
             "Types and extension methods are matched syntactically by name, without symbol binding. Aliases, inherited attributes, assembly scanning and external registration methods may be missed.",
             "C# with conditional compilation or syntax errors is skipped. Generated *.g.cs and *.generated.cs are excluded.",
             "Only appsettings*.json and the selected section are inspected. Environment overrides, secrets, config binding and runtime exporters are not evaluated. No configuration values or source excerpts are returned.",
             "Bundled baseline describes the rule reference source; it does not identify the application's installed Neo version."]);
    }

    private void Scan(string directory, int depth)
    {
        RejectLinks(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if (++entries > MaxEntries) throw new InvalidOperationException("Doctor scan exceeds 5000 entries; choose a smaller application root.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { Skip(path); continue; }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Ignored.Contains(Path.GetFileName(path))) continue;
                if (depth >= 20) throw new InvalidOperationException("Doctor directory depth limit exceeded.");
                Scan(path, depth + 1);
                continue;
            }
            var name = Path.GetFileName(path);
            bool source = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            bool config = name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (!source && !config) continue;
            if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)) continue;
            RejectLinks(path);
            var length = new FileInfo(path).Length;
            if (length > MaxFileBytes) { Skip(path); continue; }
            if (bytesRead + length > MaxTotalBytes) throw new InvalidOperationException("Doctor read budget exceeds 8 MiB; choose a smaller application root.");
            // Bound the read even if a file grows after the size check.
            using var stream = File.OpenRead(path);
            var buffer = new byte[MaxFileBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxFileBytes) { Skip(path); continue; }
            bytesRead += count;
            if (bytesRead > MaxTotalBytes) throw new InvalidOperationException("Doctor read budget exceeds 8 MiB.");
            var text = Encoding.UTF8.GetString(buffer, 0, count).TrimStart('\uFEFF');
            filesRead++;
            if (config) { AnalyzeConfig(path, text); continue; }
            var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
            var node = tree.GetCompilationUnitRoot();
            if (tree.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error)
                || node.DescendantTrivia(descendIntoTrivia: true).Any(x => x.GetStructure() is IfDirectiveTriviaSyntax))
            {
                Skip(path);
                Add("NEO-SCAN001", "info", "coverage-gap", "C# parsing", "This file was not analyzed because it has syntax errors or conditional compilation.",
                    "Inspect this file under the application's actual compiler settings before drawing conclusions.", new DoctorEvidence(Relative(path), 1, "C# analysis skipped."));
                continue;
            }
            units.Add(new(path, node));
        }
    }

    private void AnalyzeCode()
    {
        var declarations = units.SelectMany(u => u.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Select(n => (Unit: u, Node: n))).GroupBy(x => x.Node.Identifier.ValueText)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var calls = units.SelectMany(u => u.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(n => new Call(u, n, InvocationName(n)))).ToArray();
        var registrations = calls.Where(c => c.Name is "AddScoped" or "AddTransient" or "AddSingleton"
            or "TryAddScoped" or "TryAddTransient" or "TryAddSingleton" or "AddScopedWithTelemetry").ToArray();
        var proxies = registrations.Where(c => c.Name == "AddScopedWithTelemetry" && Types(c.Node).Length == 2).ToArray();

        bool Manual(string name) => declarations.TryGetValue(name, out var declaration)
            && declaration.Node.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(n => InvocationName(n) is "HandleRequest" or "HandleRequestResponse");
        DoctorEvidence? Attribute(string name)
        {
            if (!declarations.TryGetValue(name, out var declaration)) return null;
            var attribute = declaration.Node.Members.OfType<MethodDeclarationSyntax>().SelectMany(n => n.AttributeLists)
                .SelectMany(n => n.Attributes).FirstOrDefault(n => TypeName(n.Name) is "Telemetry" or "TelemetryAttribute");
            return attribute is null ? null : Evidence(declaration.Unit, attribute, "A method declares TelemetryAttribute.");
        }
        foreach (var registration in registrations.Where(c => c.Name != "AddScopedWithTelemetry"))
        {
            var types = Types(registration.Node);
            if (types.Length != 2 || Manual(types[1])) continue;
            var attribute = Attribute(types[1]) ?? Attribute(types[0]);
            if (attribute is null) continue;
            Add("NEO-TEL001", "warning", "syntax-candidate", types[0], "An annotated service has a direct DI registration that may bypass telemetry interception.",
                "Confirm the active registration and lifetime. For a scoped interface service, consider AddScopedWithTelemetry; avoid double instrumentation if a manual wrapper is intended.",
                Evidence(registration.Unit, registration.Node, "Direct generic DI registration."), attribute);
        }
        foreach (var proxy in proxies)
        {
            foreach (var dependency in new[] { "ITelementryBehaviour", "ITelementryObject", "IRequesterUser" })
            {
                bool domainProvides = dependency != "IRequesterUser" && calls.Any(c => c.Name == "AddNeoDomainServices");
                bool explicitRegistration = registrations.Any(c => Types(c.Node).FirstOrDefault() == dependency);
                if (domainProvides || explicitRegistration) continue;
                Add("NEO-DI001", "info", "missing-evidence", dependency, "No recognized registration for this dependency was found in the scanned files.",
                    "Follow custom registration extensions and external modules, then resolve the proxied service inside a DI scope. Add the dependency only if it is actually missing.",
                    Evidence(proxy.Unit, proxy.Node, "AddScopedWithTelemetry requires the telemetry DI graph."));
            }
        }
        var proxiedTypes = proxies.Select(c => Types(c.Node)[1]).ToHashSet(StringComparer.Ordinal);
        foreach (var unit in units)
        foreach (var creation in unit.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var type = TypeName(creation.Type);
            if (!proxiedTypes.Contains(type) || Manual(type)) continue;
            Add("NEO-TEL002", "warning", "syntax-candidate", type, "A type registered with telemetry is also constructed directly with new.",
                "Inspect how this instance is used. Calls intended to be intercepted must use the registered interface; a target deliberately wrapped by a proxy factory is valid.",
                Evidence(unit, creation.Type, "Explicit construction of a proxied implementation type."));
        }
        var telemetryCall = proxies.FirstOrDefault() ?? calls.FirstOrDefault(c => c.Name is "HandleRequest" or "HandleRequestResponse");
        if (telemetryCall is not null && !calls.Any(c => c.Name is "AddNeoOpenTelementry" or "AddSource" or "AddActivityListener"))
            Add("NEO-TEL003", "info", "missing-evidence", "ActivitySource listener", "No recognized trace subscription was found beside telemetry usage.",
                "Check external OpenTelemetry setup or ActivityListener registration. Trace absence cannot be proved from this scan; verify a span through a listener at runtime.",
                Evidence(telemetryCall.Unit, telemetryCall.Node, "Telemetry invocation or proxy registration is present."));
    }

    private void AnalyzeConfig(string path, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
            var node = document.RootElement;
            if (section.Length != 0)
                foreach (var part in section.Split(':'))
                {
                    if (node.ValueKind != JsonValueKind.Object) return;
                    var matches = node.EnumerateObject().Where(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) return;
                    node = matches[0].Value;
                }
            if (node.ValueKind != JsonValueKind.Object) return;
            var pointer = section.Length == 0 ? "" : "/" + string.Join('/', section.Split(':').Select(EscapePointer));
            foreach (var property in node.EnumerateObject())
            {
                bool name = property.Name.Equals("ApplicationName", StringComparison.OrdinalIgnoreCase);
                bool endpoint = property.Name.Equals("OtlpExporterEndpoint", StringComparison.OrdinalIgnoreCase);
                if (!name && !endpoint) continue;
                var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                bool wrongType = property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null);
                bool invalid = name ? string.IsNullOrWhiteSpace(value) : wrongType || !string.IsNullOrWhiteSpace(value)
                    && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host));
                if (!invalid) continue;
                Add("NEO-CONFIG001", "warning", "configuration-candidate", name ? "ApplicationName" : "OtlpExporterEndpoint",
                    "A selected telemetry configuration value is empty or invalid for its expected shape.",
                    "Check the bound configuration section and environment overrides. Use a nonblank application name and an absolute HTTP(S) OTLP endpoint when exporting.",
                    new DoctorEvidence(Relative(path), 1, "Selected telemetry key has an invalid value; the value is withheld.", pointer + "/" + EscapePointer(property.Name)));
            }
        }
        catch (JsonException)
        {
            Skip(path);
            Add("NEO-SCAN002", "info", "coverage-gap", "JSON parsing", "An appsettings file could not be parsed within the JSON depth limit.",
                "Validate JSON syntax; Doctor does not return parser messages that could contain configuration values.", new DoctorEvidence(Relative(path), 1, "Configuration analysis skipped."));
        }
    }

    private void Add(string code, string severity, string certainty, string subject, string message, string suggestion, params DoctorEvidence[] evidence)
    {
        if (findings.Count >= 100) throw new InvalidOperationException("Doctor finding limit exceeded; choose a smaller application root.");
        findings.Add(new(code, severity, certainty, subject, message, suggestion, evidence));
    }
    private void Skip(string path) { incomplete = true; skipped.Add(Relative(path)); }
    private string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private DoctorEvidence Evidence(Unit unit, SyntaxNode node, string observation) =>
        new(Relative(unit.Path), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, observation);
    private static string EscapePointer(string text) => text.Replace("~", "~0").Replace("/", "~1");
    private static string TypeName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => TypeName(q.Right), AliasQualifiedNameSyntax a => TypeName(a.Name),
        SimpleNameSyntax s => s.Identifier.ValueText, _ => ""
    };
    private static SimpleNameSyntax? CallName(InvocationExpressionSyntax node) => node.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name, SimpleNameSyntax simple => simple, _ => null
    };
    private static string InvocationName(InvocationExpressionSyntax node) => CallName(node)?.Identifier.ValueText ?? "";
    private static string[] Types(InvocationExpressionSyntax node) => CallName(node) is GenericNameSyntax generic
        ? generic.TypeArgumentList.Arguments.Select(TypeName).ToArray() : [];
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Doctor does not follow symbolic links or junctions.");
    }
    private sealed record Unit(string Path, CompilationUnitSyntax Root);
    private sealed record Call(Unit Unit, InvocationExpressionSyntax Node, string Name);
}
