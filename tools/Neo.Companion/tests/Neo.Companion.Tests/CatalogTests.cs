using System.Text.Json;
using Neo.Companion.Mcp;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "neo-inspect-" + Guid.NewGuid().ToString("N"));
    public CatalogTests() => Directory.CreateDirectory(root);
    private CompanionCatalog Catalog() => new(AppContext.BaseDirectory, root);

    [Fact]
    public void Search_returns_real_contract_and_baseline()
    {
        var result = JsonSerializer.SerializeToElement(Catalog().SearchDocs("ICommandRepository", 8));
        Assert.StartsWith("sha256:", result.GetProperty("baseline").GetString());
        Assert.Contains(result.GetProperty("results").EnumerateArray(), x => x.GetProperty("Path").GetString()!.EndsWith("ICommandRepository.cs"));
    }

    [Fact]
    public void Example_rejects_other_versions_and_returns_source_for_supported_baseline()
    {
        var catalog = Catalog();
        Assert.Throws<ArgumentException>(() => catalog.GetExample("product-create", "10.0.0"));
        Assert.Throws<ArgumentException>(() => catalog.GetExample("../secret", catalog.Baseline));
        var result = JsonSerializer.SerializeToElement(catalog.GetExample("product-create", catalog.Baseline));
        Assert.Contains(result.GetProperty("files").EnumerateArray(), x => x.GetProperty("path").GetString() == "Application/CreateProduct.cs");
    }

    [Fact]
    public void Inspector_reports_central_versions_without_evaluating_project_code()
    {
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project><ItemGroup><PackageReference Include='Neo.Domain'/><ProjectReference Include='../outside/Other.csproj'/></ItemGroup><Target Name='NeverRun'><Error Text='Do not evaluate'/></Target></Project>");
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project><ItemGroup><PackageVersion Include='Neo.Domain' Version='10.0.8'/></ItemGroup></Project>");
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        File.WriteAllText(Path.Combine(root, "obj", "Bad.csproj"), "not xml");
        File.WriteAllText(Path.Combine(root, "appsettings.json"), "not a file the inspector should parse");
        var result = JsonSerializer.SerializeToElement(Catalog().InspectProject());
        Assert.Equal("static-declarations", result.GetProperty("mode").GetString());
        Assert.Single(result.GetProperty("projects").EnumerateArray());
        Assert.Contains("10.0.8", result.GetRawText());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("projects")[0].GetProperty("packageReferences")[0].GetProperty("version").ValueKind);
    }

    [Fact]
    public void Inspector_rejects_external_xml_entities()
    {
        File.WriteAllText(Path.Combine(root, "Bad.csproj"), "<!DOCTYPE Project [<!ENTITY secret SYSTEM 'file:///outside.txt'>]><Project>&secret;</Project>");
        Assert.Throws<System.Xml.XmlException>(() => Catalog().InspectProject());
    }

    [Fact]
    public void Search_rejects_unbounded_requests()
    {
        Assert.Throws<ArgumentException>(() => Catalog().SearchDocs("", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Catalog().SearchDocs("repository", 500));
        var empty = JsonSerializer.SerializeToElement(Catalog().SearchDocs("no-such-api-2389", 5));
        Assert.Empty(empty.GetProperty("results").EnumerateArray());
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("attribute")]
    public void Telemetry_recipe_returns_real_sample_and_rejects_wrong_versions(string mode)
    {
        var catalog = Catalog();
        var recipe = JsonSerializer.SerializeToElement(catalog.GetTelemetryRecipe(mode, catalog.Baseline));
        Assert.Contains(recipe.GetProperty("files").EnumerateArray(), x => x.GetProperty("path").GetString() == "ProductLookup.cs");
        Assert.Contains("ITelementryBehaviour", recipe.GetProperty("guide").GetString());
        Assert.Throws<ArgumentException>(() => catalog.GetTelemetryRecipe(mode, "old-version"));
        Assert.Throws<ArgumentException>(() => catalog.GetTelemetryRecipe("invalid", catalog.Baseline));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
