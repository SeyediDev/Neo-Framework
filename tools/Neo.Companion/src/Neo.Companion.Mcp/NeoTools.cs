using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Neo.Companion.Mcp;

[McpServerToolType]
public sealed class NeoTools(CompanionCatalog catalog)
{
    [McpServerTool(Name = "neo_diagnose_project", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect C# syntax and selected appsettings keys under NEO_PROJECT_ROOT for Neo DI and telemetry issues. Returns review candidates with file locations, evidence, suggestions and coverage limits. Does not execute code, evaluate MSBuild, resolve symbols, return config values or prove runtime health. Configure the root to one consuming application.")]
    public string DiagnoseProject(
        [Description("Telemetry JSON section, colon-separated; defaults to TelemetryOptions. Use an empty string for root-level options. Environment overrides are not evaluated.")] string configurationSection = "TelemetryOptions")
        => JsonSerializer.Serialize(catalog.DiagnoseProject(configurationSection), JsonSerializerOptions.Web);

    [McpServerTool(Name = "neo_inspect_project", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect .NET project declarations under the server-configured NEO_PROJECT_ROOT. Reads csproj and Directory props only; does not execute MSBuild. Reports declared versions, not evaluated versions.")]
    public string InspectProject() => JsonSerializer.Serialize(catalog.InspectProject());

    [McpServerTool(Name = "neo_search_docs", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search bundled Neo guidance and source contracts. Returns source paths, line numbers and a pinned baseline. A match is evidence for that baseline only, not for other Neo versions.")]
    public string SearchDocs([Description("Words or an API name, e.g. ICommandRepository, repository, محصول.")] string query,
        [Description("Maximum results, 1 through 20.")] int limit = 8) => JsonSerializer.Serialize(catalog.SearchDocs(query, limit));

    [McpServerTool(Name = "neo_get_example", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Get the ProductCatalog reference source for an exact baseline. First obtain baseline from neo_search_docs. If your project's source or package version differs, inspect the actual contracts before adapting the example; do not infer compatibility.")]
    public string GetExample([Description("Supported example ID: product-create.")] string example,
        [Description("Exact source baseline identifier returned by neo_search_docs.")] string baseline)
        => JsonSerializer.Serialize(catalog.GetExample(example, baseline));

    [McpServerTool(Name = "neo_get_telemetry_recipe", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Get a source-backed, runnable Neo telemetry recipe. Manual mode uses ITelementryBehaviour; attribute mode uses TelemetryAttribute and the active AddScopedWithTelemetry proxy. Returns DI setup, exporter guidance, sample source and compatibility constraints. It does not read production traces or modify your project.")]
    public string GetTelemetryRecipe(
        [Description("manual or attribute")] string mode,
        [Description("Exact baseline returned by neo_search_docs")] string baseline)
        => JsonSerializer.Serialize(catalog.GetTelemetryRecipe(mode, baseline));
}
