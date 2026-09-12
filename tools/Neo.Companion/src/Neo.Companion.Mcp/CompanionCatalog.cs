using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Neo.Companion.Mcp;

public sealed class CompanionCatalog
{
    private readonly string contentRoot;
    private readonly string? projectRoot;
    private const int MaxFileBytes = 512 * 1024;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", "node_modules", ".neo", ".local" };
    public string Baseline { get; }

    public CompanionCatalog(string contentRoot, string? projectRoot)
    {
        this.contentRoot = Path.GetFullPath(contentRoot);
        this.projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        using var manifest = JsonDocument.Parse(ReadText(Path.Combine(this.contentRoot, "knowledge", "manifest.json")));
        Baseline = manifest.RootElement.GetProperty("baseline").GetString()!;
    }

    public DoctorReport DiagnoseProject(string configurationSection = "TelemetryOptions")
    {
        if (projectRoot is null || !Directory.Exists(projectRoot))
            throw new InvalidOperationException("Set NEO_PROJECT_ROOT to an existing application directory before starting the server.");
        ArgumentNullException.ThrowIfNull(configurationSection);
        return new NeoDoctor(projectRoot, Baseline, configurationSection).Run();
    }

    public object InspectProject()
    {
        if (projectRoot is null || !Directory.Exists(projectRoot))
            throw new InvalidOperationException("Set NEO_PROJECT_ROOT to an existing project directory before starting the server.");
        RejectLinkedAncestors(projectRoot);
        var projects = new List<object>();
        var properties = new List<object>();
        foreach (var file in EnumerateFiles(projectRoot, 5000))
        {
            var name = Path.GetFileName(file);
            if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && name != "Directory.Build.props" && name != "Directory.Packages.props") continue;
            using var reader = XmlReader.Create(new StringReader(ReadText(file)), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var doc = XDocument.Load(reader);
            var path = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            string? Value(XElement element, string key) => (string?)element.Attribute(key)
                ?? element.Elements().FirstOrDefault(x => x.Name.LocalName == key)?.Value;
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projects.Add(new
                {
                    path,
                    targetFrameworkDeclarations = doc.Descendants().Where(x => x.Name.LocalName is "TargetFramework" or "TargetFrameworks").Select(x => x.Value).ToArray(),
                    packageReferences = doc.Descendants().Where(x => x.Name.LocalName == "PackageReference")
                        .Select(x => new { name = Value(x, "Include") ?? Value(x, "Update"), version = Value(x, "VersionOverride") ?? Value(x, "Version"), condition = (string?)x.Attribute("Condition") }).ToArray(),
                    projectReferences = doc.Descendants().Where(x => x.Name.LocalName == "ProjectReference")
                        .Select(x => (string?)x.Attribute("Include")).ToArray()
                });
            }
            else properties.Add(new
            {
                path,
                declarations = doc.Descendants().Where(x => x.Parent?.Name.LocalName == "PropertyGroup"
                    && x.Name.LocalName is "Version" or "TargetFramework" or "TargetFrameworks" or "ManagePackageVersionsCentrally" or "EfVersion" or "DotNetVersion")
                    .Select(x => new { name = x.Name.LocalName, value = x.Value, condition = (string?)x.Parent?.Attribute("Condition") }).ToArray(),
                neoPackageVersions = doc.Descendants().Where(x => x.Name.LocalName == "PackageVersion"
                    && ((string?)x.Attribute("Include"))?.StartsWith("Neo.", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(x => new { name = (string?)x.Attribute("Include"), version = Value(x, "Version") }).ToArray()
            });
        }
        return new { mode = "static-declarations", projects, properties,
            note = "Imports, MSBuild property expansion and conditions are not evaluated. A null version may be centrally managed. External project references are reported but not followed. No installed Neo version or compatibility is inferred from these declarations.",
            bundledBaseline = Baseline };
    }

    public object SearchDocs(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            throw new ArgumentException("Query must contain 1–200 characters.", nameof(query));
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = new List<SearchHit>();
        var root = Path.Combine(contentRoot, "knowledge");
        foreach (var file in EnumerateFiles(root, 500))
        {
            if (Path.GetExtension(file) is not (".md" or ".cs")) continue;
            var lines = ReadText(file).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                int score = terms.Count(term => lines[i].Contains(term, StringComparison.OrdinalIgnoreCase));
                if (score == 0) continue;
                hits.Add(new SearchHit(Path.GetRelativePath(root, file).Replace('\\', '/'), i + 1,
                    lines[i].TrimEnd('\r'), score));
            }
        }
        return new { baseline = Baseline, query, results = hits.OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Line).Take(limit).ToArray(),
            note = "Literal word search over the bundled snapshot. Source contracts take precedence over prose. A zero-result search is not proof that an API is absent." };
    }

    public object GetExample(string example, string baseline)
    {
        if (example != "product-create") throw new ArgumentException("Unknown example. Available: product-create.", nameof(example));
        if (!string.Equals(baseline, Baseline, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported baseline. Available: {Baseline}. Do not assume another version is compatible.", nameof(baseline));
        var root = Path.Combine(contentRoot, "examples", "ProductCatalog");
        var files = EnumerateFiles(root, 50).Where(x => Path.GetExtension(x) is ".cs" or ".csproj")
            .Order(StringComparer.Ordinal).Select(x => new { path = Path.GetRelativePath(root, x).Replace('\\', '/'), content = ReadText(x) }).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("Bundled example files are missing; rebuild the MCP project.");
        return new { example, baseline = Baseline, files,
            prerequisites = ".NET 10 SDK; Neo source checkout matching the bundled contract snapshot. Set MSBuild NeoRoot to that checkout. The source repository's Directory.Build.props is not part of an individual sample project: the Companion root supplies net10.0 and NeoRoot.",
            scope = "Local teaching sample: SQLite, explicit MediatR registration and handler validation. Does not enable Neo authorization/telemetry pipelines, authentication, Outbox or production migrations." };
    }

    public object GetTelemetryRecipe(string mode, string baseline)
    {
        if (mode is not ("manual" or "attribute")) throw new ArgumentException("Mode must be manual or attribute.", nameof(mode));
        if (!string.Equals(baseline, Baseline, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported baseline. Available: {Baseline}.", nameof(baseline));
        var root = Path.Combine(contentRoot, "examples", "TelemetryDemo");
        var files = EnumerateFiles(root, 50).Where(x => Path.GetExtension(x) is ".cs" or ".csproj")
            .Order(StringComparer.Ordinal).Select(x => new { path = Path.GetRelativePath(root, x).Replace('\\', '/'), content = ReadText(x) }).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("Telemetry example is missing; rebuild the MCP project.");
        return new { mode, baseline = Baseline, files,
            guide = ReadText(Path.Combine(contentRoot, "knowledge", "telemetry-guide.md")),
            registration = mode == "attribute" ? "AddScopedWithTelemetry<IProductLookup, ProductLookup>()" : "AddScoped<IProductLookup, ManualProductLookup>()",
            run = $"dotnet run --project tools/Neo.Companion/samples/TelemetryDemo -- {mode}",
            compatibility = "This snapshot includes runtime fixes. Older Neo revisions had disabled proxy registration and broken async interception. Check the actual installed code before using the attribute recipe." };
    }

    private static IEnumerable<string> EnumerateFiles(string root, int maxEntries)
    {
        RejectLinkedAncestors(root);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var entries = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path).Order(StringComparer.Ordinal))
            {
                if (++entries > maxEntries) throw new InvalidOperationException("Scan limit exceeded; configure a smaller project root.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!Ignored.Contains(Path.GetFileName(entry)))
                    {
                        if (current.Depth >= 20) throw new InvalidOperationException("Directory nesting limit exceeded.");
                        pending.Push((entry, current.Depth + 1));
                    }
                }
                else yield return entry;
            }
        }
    }

    private static string ReadText(string path)
    {
        RejectLinkedAncestors(path);
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidOperationException("File exceeds the 512 KiB read limit.");
        return File.ReadAllText(path);
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic links and junctions are not supported as content/project roots.");
    }

    private sealed record SearchHit(string Path, int Line, string Text, int Score);
}
