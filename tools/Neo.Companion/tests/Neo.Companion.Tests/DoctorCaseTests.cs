using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.Companion.Mcp;
using Neo.Infrastructure.Features.Telementry;
using OpenTelemetry.Trace;
using Xunit;
using Direct = Neo.Companion.DoctorCases.DirectRegistration;
using Missing = Neo.Companion.DoctorCases.MissingRequester;
using Healthy = Neo.Companion.DoctorCases.Healthy;

namespace Neo.Companion.Tests;

[Collection("Telemetry")]
public sealed class DoctorCaseTests
{
    private static string CasePath(string name) => Path.Combine(AppContext.BaseDirectory, "DoctorCases", name);
    private static DoctorReport Diagnose(string name) => new CompanionCatalog(AppContext.BaseDirectory, CasePath(name)).DiagnoseProject();

    [Fact]
    public async Task Direct_registration_returns_business_result_but_loses_span_and_doctor_reports_it()
    {
        var spans = new List<Activity>();
        using var listener = Direct.Setup.Listen(spans.Add);
        using var provider = Direct.Setup.Build();
        using var scope = provider.CreateScope();
        Assert.Equal("found", await scope.ServiceProvider.GetRequiredService<Direct.IOrders>().Lookup());
        Assert.Empty(spans);
        Assert.Contains(Diagnose("DirectRegistration").Findings, f => f.Code == "NEO-TEL001");
    }

    [Fact]
    public void Missing_requester_fails_service_resolution_and_doctor_points_to_dependency()
    {
        using var provider = Missing.Setup.Build();
        using var scope = provider.CreateScope();
        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<Missing.IOrders>());
        Assert.Contains("IRequesterUser", error.Message);
        Assert.Contains(Diagnose("MissingRequester").Findings, f => f.Code == "NEO-DI001" && f.Subject == "IRequesterUser");
    }

    [Fact]
    public async Task Corrected_registration_resolves_and_emits_span_without_doctor_findings()
    {
        var spans = new List<Activity>();
        using var listener = Healthy.Setup.Listen(spans.Add);
        using var provider = Healthy.Setup.Build();
        using var scope = provider.CreateScope();
        Assert.Equal("found", await scope.ServiceProvider.GetRequiredService<Healthy.IOrders>().Lookup());
        var span = Assert.Single(spans);
        Assert.Equal("orders.lookup", span.DisplayName);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Empty(Diagnose("Healthy").Findings);
    }

    [Fact]
    public void Invalid_endpoint_breaks_export_setup_and_doctor_reports_selected_key()
    {
        var config = new ConfigurationBuilder().SetBasePath(CasePath("InvalidConfig")).AddJsonFile("appsettings.json").Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeoOpenTelementry(config.GetSection("TelemetryOptions"));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<UriFormatException>(() => provider.GetRequiredService<TracerProvider>());
        var finding = Assert.Single(Diagnose("InvalidConfig").Findings);
        Assert.Equal("NEO-CONFIG001", finding.Code);
        Assert.Equal("OtlpExporterEndpoint", finding.Subject);
    }
}
