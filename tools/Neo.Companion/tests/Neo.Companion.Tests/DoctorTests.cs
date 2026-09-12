using System.Text.Json;
using Neo.Companion.Mcp;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class DoctorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "neo-doctor-" + Guid.NewGuid().ToString("N"));
    public DoctorTests() => Directory.CreateDirectory(root);
    private void Write(string path, string text)
    {
        var destination = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, text);
    }
    private DoctorReport Diagnose(string section = "TelemetryOptions") => new CompanionCatalog(AppContext.BaseDirectory, root).DiagnoseProject(section);

    [Fact]
    public void Reports_direct_registration_with_correct_line_and_both_evidence_locations()
    {
        Write("Contract.cs", "public interface IOrder { [Telemetry] Task Run(); }");
        Write("Startup.cs", "// boot\nservices.AddScoped<IOrder, Order>();");
        var finding = Assert.Single(Diagnose().Findings);
        Assert.Equal("NEO-TEL001", finding.Code);
        Assert.Equal("syntax-candidate", finding.Certainty);
        Assert.Equal("Startup.cs", finding.Evidence[0].Path);
        Assert.Equal(2, finding.Evidence[0].Line);
        Assert.Equal("Contract.cs", finding.Evidence[1].Path);
    }

    [Fact]
    public void Comments_strings_generated_files_and_disabled_code_do_not_become_registrations()
    {
        Write("Startup.cs", "// services.AddScopedWithTelemetry<IA, A>();\nvar text = \"services.AddScopedWithTelemetry<IA, A>();\";");
        Write("Generated.g.cs", "services.AddScopedWithTelemetry<IA, A>();");
        Write("obj/Bad.cs", "services.AddScopedWithTelemetry<IA, A>();");
        Assert.Empty(Diagnose().Findings);
        Write("Conditional.cs", "#if EXTERNAL\nservices.AddScopedWithTelemetry<IA, A>();\n#endif");
        var result = Diagnose();
        Assert.Equal("incomplete", result.Status);
        Assert.Equal("NEO-SCAN001", Assert.Single(result.Findings).Code);
    }

    [Fact]
    public void Missing_registration_is_only_missing_evidence_and_domain_extension_supplies_two_dependencies()
    {
        Write("Startup.cs", "services.AddNeoDomainServices(config);\nservices.AddScopedWithTelemetry<IA, A>();\ntracing.AddSource(\"app\");");
        var result = Diagnose();
        var missing = Assert.Single(result.Findings);
        Assert.Equal("NEO-DI001", missing.Code);
        Assert.Equal("IRequesterUser", missing.Subject);
        Assert.Equal("missing-evidence", missing.Certainty);
        Write("UserRegistration.cs", "services.AddScoped<IRequesterUser>(sp => CreateUser());");
        Assert.Empty(Diagnose().Findings);
        Assert.Equal("review-required", Diagnose().Status);
    }

    [Fact]
    public void Recognizes_qualified_interface_and_implementation_attributes_and_manual_wrappers()
    {
        Write("Service.cs", "namespace App; public class Order { [Neo.Domain.Features.Telementry.TelemetryAttribute] public void Run() {} }");
        Write("Startup.cs", "services.AddScoped<App.IOrder, App.Order>();");
        Assert.Equal("NEO-TEL001", Assert.Single(Diagnose().Findings).Code);
        Write("Service.cs", "namespace App; public class Order { [Telemetry] public void Run() { behaviour.HandleRequest(next, request); } }");
        Assert.DoesNotContain(Diagnose().Findings, f => f.Code == "NEO-TEL001");
    }

    [Fact]
    public void Reports_direct_construction_and_absent_trace_subscription()
    {
        Write("Startup.cs", "services.AddNeoDomainServices(config); services.AddScoped<IRequesterUser, User>();\nservices.AddScopedWithTelemetry<IA,A>();\nvar target = new A();");
        var result = Diagnose();
        Assert.Contains(result.Findings, f => f.Code == "NEO-TEL002" && f.Evidence[0].Line == 3);
        Assert.Contains(result.Findings, f => f.Code == "NEO-TEL003" && f.Certainty == "missing-evidence");
    }

    [Fact]
    public void Malformed_sources_and_oversized_files_report_incomplete_coverage()
    {
        Write("Broken.cs", "public class {");
        Write("appsettings.json", "{ \"secret\": \"private-value\", ");
        Write("Huge.cs", new string(' ', 512 * 1024 + 1));
        var result = Diagnose();
        Assert.Equal("incomplete", result.Status);
        Assert.Equal(3, result.SkippedPaths.Count);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Only_selected_json_keys_are_checked_and_values_are_never_returned()
    {
        Write("appsettings.json", """
            { "Password": "private-secret", "Other": { "ApplicationName": "" },
              "TelemetryOptions": { "ApplicationName": " ", "OtlpExporterEndpoint": "ftp://user:private-secret@host/path" } }
            """);
        var result = Diagnose();
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal("NEO-CONFIG001", f.Code));
        Assert.Contains(result.Findings, f => f.Evidence[0].JsonPointer == "/TelemetryOptions/ApplicationName");
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(result));
        Assert.Empty(Diagnose("MissingSection").Findings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:4317")]
    [InlineData("https://collector.example.test/v1/traces")]
    public void Valid_config_and_optional_empty_endpoint_produce_no_configuration_findings(string endpoint)
    {
        Write("appsettings.Development.json", JsonSerializer.Serialize(new { Observability = new { Telemetry = new { ApplicationName = "app", OtlpExporterEndpoint = endpoint } } }));
        Assert.Empty(Diagnose("Observability:Telemetry").Findings);
    }

    [Fact]
    public void Read_only_analysis_does_not_execute_projects_or_follow_external_references()
    {
        Write("App.csproj", "<!DOCTYPE Project><Project><Target Name='Build'><Error Text='Never execute'/></Target></Project>");
        Write("Startup.cs", "services.AddScopedWithTelemetry<IA, A>();");
        var before = File.ReadAllText(Path.Combine(root, "Startup.cs"));
        var result = Diagnose();
        Assert.Equal(1, result.FilesRead);
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, "Startup.cs")));
        Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        Assert.Throws<ArgumentException>(() => Diagnose("a::b"));
    }

    [Fact]
    public void Duplicate_simple_type_names_are_not_used_as_attribute_evidence()
    {
        Write("A.cs", "namespace One { public interface IA { [Telemetry] void Run(); } }");
        Write("B.cs", "namespace Two { public interface IA { void Run(); } }");
        Write("Startup.cs", "services.AddScoped<IA,A>();");
        Assert.Empty(Diagnose().Findings);
    }

    [Fact]
    public void Finding_budget_fails_explicitly_instead_of_returning_a_partial_success()
    {
        Write("Many.cs", string.Join("\n", Enumerable.Range(0, 40).Select(i => $"services.AddScopedWithTelemetry<IService{i}, Service{i}>();")));
        Assert.Throws<InvalidOperationException>(() => Diagnose());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
