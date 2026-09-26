using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace Neo.Companion.Cli;

public static class FeatureGenerator
{
    private static readonly string[] Templates = ["Program.cs", "ProductResource.cs", "DemoAuthentication.cs"];
    private static bool Identifier(string value) => value.Length is > 0 and <= 64
        && Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)
        && SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(value) == SyntaxKind.None;

    public static string Generate(string name, string targetNamespace, string route, string output, string neoRoot)
    {
        if (!Identifier(name)) throw new ArgumentException("Name must be an ASCII C# identifier of 1..64 characters, not a keyword.");
        if (targetNamespace.Length > 160 || !targetNamespace.Split('.').All(Identifier)) throw new ArgumentException("Invalid C# namespace.");
        if (!Regex.IsMatch(route, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Route must be a lowercase literal segment of 1..64 characters.");
        var sources = Templates.ToDictionary(x => x, ReadTemplate);
        var reserved = sources.Values.SelectMany(x => CSharpSyntaxTree.ParseText(x).GetRoot().DescendantTokens())
            .Where(x => x.RawKind == (int)SyntaxKind.IdentifierToken && !x.ValueText.Contains("Product", StringComparison.Ordinal))
            .Select(x => x.ValueText).ToHashSet(StringComparer.Ordinal);
        var declared = new[] { name, "Create" + name, "Update" + name, name + "View", name + "Definition", name + "sController", name + "Translation", name + "Changes" };
        if (declared.Any(reserved.Contains) || targetNamespace.Split('.').Any(reserved.Contains))
            throw new ArgumentException("Name or namespace collides with a template/framework identifier; choose an application-specific name.");
        var root = Path.GetFullPath(neoRoot);
        var destination = Path.GetFullPath(output);
        RejectLinks(root); RejectLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Output must be a new directory; existing output is never overwritten.");
        foreach (var relative in new[] { "src/Neo.Endpoint/Controller/Base/GenericCrudResourceControllerBase.cs", "src/Neo.Infrastructure/Features/Crud/EfCrudService.cs", "src/Neo.Infrastructure/Features/Crud/CrudRuntimeDoctor.cs" })
            if (!File.Exists(Path.Combine(root, relative))) throw new ArgumentException("NeoRoot must be a matching source checkout containing CRUD resource APIs.");
        if (root.Any(char.IsControl) || destination.Any(char.IsControl)) throw new ArgumentException("Control characters are not supported in paths.");
        var files = new Dictionary<string,string>();
        foreach (var source in sources)
            files[source.Key.Replace("Product", name, StringComparison.Ordinal)] = source.Value
                .Replace("Product", name, StringComparison.Ordinal).Replace("products", route, StringComparison.Ordinal)
                .Replace("CrudResourceDemo", targetNamespace, StringComparison.Ordinal).Replace("catalog.write", route + ".write", StringComparison.Ordinal);
        files[name + ".csproj"] = ReadTemplate("CrudResourceDemo.csproj");
        files["Directory.Build.props"] = $"""
            <Project><PropertyGroup>
              <TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
              <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              <NeoRoot>{SecurityElement.Escape(EscapeMsBuild(root))}</NeoRoot>
            </PropertyGroup></Project>
            """;
        files["Directory.Packages.props"] = "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>\n";
        files[".gitignore"] = "bin/\nobj/\n*.db\n*.db-shm\n*.db-wal\n";
        files["README.md"] = $$"""
            # {{name}} resource

            Generated from Neo's tested CrudResourceDemo. Requires .NET 10 and the Neo source checkout in Directory.Build.props.
            No claim is made that existing NuGet releases contain these APIs. Review and commit this generated source in your application.

            Run `dotnet run --project . -- --doctor` for metadata/DI checks without creating a database.
            Run `dotnet run --project .` to start the local teaching app on http://127.0.0.1:5092.
            SQLite creates neo-crud-demo.db in the working directory. Set DemoToken to your own runtime-generated token before startup.
            GET /{{route}} is public; writes require Authorization: Bearer followed by that token. No token is baked into source.
            POST /{{route}} accepts {"name":"Example","persianName":null} and returns id/version/data plus Location.
            PUT /{{route}}/{id} accepts {"data":{"name":"Updated","persianName":null},"expectedVersion":"version-from-read"}.
            DELETE /{{route}}/{id}?expectedVersion={version} requires the latest token. A stale write returns 409/stale_version.

            Explicit mapping preserves server-owned fields. Translation and the Outbox row share the entity transaction.
            Set DemonstrateRollback=true to exercise a failure after staging; all three changes roll back.
            The Outbox message is illustrative and has no worker/handler here; adapt Neo's HangfireOutboxDemo or DurableMessagingDemo for dispatch.
            Pessimistic mode needs Concurrency=Pessimistic and ConnectionStrings__Demo pointing to an isolated SQL Server database named NeoCrudDemo*.
            Optimistic is the default. Do not use automatic EF retries; use fresh scoped contexts. Never silently retry a stale form with a new version.
            Before production, configure your identity provider, tenant/ownership scope, migrations and any soft-delete requirements.
            The scaffold uses EnsureCreated only for a new demo database and hard deletes records. Doctor is not physical-schema verification.
            """;

        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Output needs a parent directory.");
        Directory.CreateDirectory(parent); RejectLinks(parent);
        var staging = Path.Combine(parent, ".neo-generate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files) File.WriteAllText(Path.Combine(staging, file.Key), file.Value, new UTF8Encoding(false));
            RejectLinks(parent);
            Directory.Move(staging, destination); // Atomic publication to a new name; never merge into existing content.
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                RejectLinks(staging);
                foreach (var file in files.Keys) File.Delete(Path.Combine(staging, file));
                Directory.Delete(staging);
            }
        }
    }

    private static string ReadTemplate(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("template." + name)
            ?? throw new InvalidOperationException("Generator template is missing; rebuild the CLI.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static string EscapeMsBuild(string value) => string.Concat(value.Select(c => "%$@;'()?*".Contains(c) ? "%" + ((int)c).ToString("X2") : c.ToString()));
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links and junctions are not supported in input/output paths."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
