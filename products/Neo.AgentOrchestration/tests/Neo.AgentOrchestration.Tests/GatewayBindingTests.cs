using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class GatewayBindingTests
{
    private static readonly ExternalAgentScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Profile = Guid.NewGuid();
    private static string Callback => $"https://callback.invalid/api/orchestration/v1/organizations/{Scope.OrganizationId:D}/workspaces/{Scope.WorkspaceId:D}/harness/coding/runs/{Scope.RunId:D}/result";
    [Theory]
    [InlineData("NeoAgentOrchestration")]
    [InlineData("WorkManagement")]
    [InlineData("master")]
    [InlineData("FanasaAgentGatewayWrong")]
    public void Gateway_provisioning_refuses_other_catalogs(string database)
        => Assert.Throws<ArgumentException>(() => GatewayProvisioner.ValidateDestination("Server=localhost;Database=" + database + ";Integrated Security=true", database));
    [Fact]
    public void Internal_composition_has_disabled_sandbox_and_does_not_start_a_host()
    {
        using var source = Services(); var config = source.GetRequiredService<IConfiguration>();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton(config); services.AddSingleton<IHostEnvironment>(new Environment());
        services.AddSingleton<IHarnessSecrets>(new Secrets(false, false));
        services.AddGatewayJournal(config, "Server=localhost;Database=FanasaAgentGateway;Integrated Security=true");
        using var composed = services.BuildServiceProvider(); using var scope = composed.CreateScope();
        Assert.IsType<DisabledGatewaySandbox>(scope.ServiceProvider.GetRequiredService<Neo.AgentOrchestration.Application.ExternalExecution.IGatewaySandbox>());
        Assert.IsType<HttpGatewayResultDelivery>(scope.ServiceProvider.GetRequiredService<Neo.AgentOrchestration.Application.ExternalExecution.IGatewayResultDelivery>());
        Assert.Empty(composed.GetServices<IHostedService>());
        Assert.Throws<ArgumentException>(() => services.AddGatewayJournal(config, "Server=localhost;Database=NeoAgentOrchestration;Integrated Security=true"));
    }
    [Theory]
    [InlineData("AgentGateway:Enabled", "false")]
    [InlineData("AgentGateway:Bindings:coding:Enabled", "false")]
    [InlineData("NativeAgents:Enabled", "false")]
    [InlineData("AgentGateway:Bindings:coding:RepositoryUrl", "https://user:password@repository.invalid/project")]
    [InlineData("AgentGateway:Bindings:coding:Revision", "develop")]
    [InlineData("AgentGateway:Bindings:coding:ImageDigest", "latest")]
    [InlineData("AgentGateway:Bindings:coding:SandboxId", "../../host")]
    [InlineData("AgentGateway:Bindings:coding:DispatchSecretRef", "env:TEST_CALLBACK")]
    [InlineData("AgentGateway:Bindings:coding:CallbackBaseUrl", "http://remote.invalid/")]
    public void Unsafe_or_disabled_binding_cannot_reserve(string field, string value)
    {
        using var services = Services(field, value);
        var error = Assert.Throws<ExternalAgentException>(() => services.GetRequiredService<ConfiguredGatewayBindings>().Resolve("coding", Scope, Profile, Callback));
        Assert.Null(error.InnerException); Assert.DoesNotContain("password@", error.ToString());
    }
    [Fact]
    public void Exact_scope_profile_callback_and_distinct_secrets_are_required()
    {
        using var services = Services(); var bindings = services.GetRequiredService<ConfiguredGatewayBindings>();
        var binding = bindings.Resolve("coding", Scope, Profile, Callback);
        Assert.Equal(64, binding.Fingerprint.Length);
        Assert.Throws<ExternalAgentException>(() => bindings.Resolve("coding", Scope, Guid.NewGuid(), Callback));
        Assert.Throws<ExternalAgentException>(() => bindings.Resolve("coding", Scope with { ProjectId = Guid.NewGuid() }, Profile, Callback));
        Assert.Throws<ExternalAgentException>(() => bindings.Resolve("coding", Scope, Profile, "https://other.invalid/result"));
        Assert.Throws<ExternalAgentException>(() => bindings.Resolve("coding", Scope, Profile, Callback + "?override=true"));
        Assert.True(bindings.Authenticate("coding", new('D', 40))); Assert.False(bindings.Authenticate("coding", new('C', 40)));
        Assert.False(bindings.Authenticate("coding", null)); Assert.False(bindings.Authenticate("../coding", new('D', 40)));
        using var equalSecrets = Services(sameValues: true);
        Assert.Throws<ExternalAgentException>(() => equalSecrets.GetRequiredService<ConfiguredGatewayBindings>().Resolve("coding", Scope, Profile, Callback));
    }
    [Fact]
    public void Repository_revision_and_native_model_changes_change_fingerprint_but_secret_rotation_does_not()
    {
        using var original = Services(); var first = original.GetRequiredService<ConfiguredGatewayBindings>().Resolve("coding", Scope, Profile, Callback);
        using var revision = Services("AgentGateway:Bindings:coding:Revision", new('b', 40));
        Assert.NotEqual(first.Fingerprint, revision.GetRequiredService<ConfiguredGatewayBindings>().Resolve("coding", Scope, Profile, Callback).Fingerprint);
        using var model = Services("NativeAgents:Connections:coding:ModelId", "another-approved-model");
        Assert.NotEqual(first.Fingerprint, model.GetRequiredService<ConfiguredGatewayBindings>().Resolve("coding", Scope, Profile, Callback).Fingerprint);
        using var rotated = Services(rotate: true);
        Assert.Equal(first.Fingerprint, rotated.GetRequiredService<ConfiguredGatewayBindings>().Resolve("coding", Scope, Profile, Callback).Fingerprint);
    }
    private static ServiceProvider Services(string? field = null, string? value = null, bool sameValues = false, bool rotate = false)
    {
        var values = new Dictionary<string, string?> {
            ["AgentGateway:Enabled"] = "true", ["AgentGateway:Bindings:coding:Enabled"] = "true",
            ["AgentGateway:Bindings:coding:AgentProfileId"] = Profile.ToString(),
            ["AgentGateway:Bindings:coding:SandboxId"] = "isolated-test",
            ["AgentGateway:Bindings:coding:Revision"] = new('a', 40),
            ["AgentGateway:Bindings:coding:RepositoryUrl"] = "https://repository.invalid/project",
            ["AgentGateway:Bindings:coding:ImageDigest"] = "sha256:" + new string('a', 64),
            ["AgentGateway:Bindings:coding:CallbackBaseUrl"] = "https://callback.invalid/",
            ["AgentGateway:Bindings:coding:DispatchSecretRef"] = "env:TEST_DISPATCH",
            ["AgentGateway:Bindings:coding:CallbackSecretRef"] = "env:TEST_CALLBACK",
            ["NativeAgents:Enabled"] = "true", ["NativeAgents:Connections:coding:Enabled"] = "true",
            ["NativeAgents:Connections:coding:Engine"] = "opencode", ["NativeAgents:Connections:coding:OrganizationId"] = Scope.OrganizationId.ToString(),
            ["NativeAgents:Connections:coding:WorkspaceId"] = Scope.WorkspaceId.ToString(),
            ["NativeAgents:Connections:coding:ProjectId"] = Scope.ProjectId.ToString(),
            ["NativeAgents:Connections:coding:Endpoint"] = "https://native.invalid/",
            ["NativeAgents:Connections:coding:SecretRef"] = "env:TEST_NATIVE",
            ["NativeAgents:Connections:coding:ModelProvider"] = "approved-provider",
            ["NativeAgents:Connections:coding:ModelId"] = "approved-model"
        };
        if (field is not null) values[field] = value;
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton<IHostEnvironment>(new Environment());
        services.AddSingleton<IHarnessSecrets>(new Secrets(sameValues, rotate)); services.AddNativeAgentAdapters();
        services.AddSingleton<ConfiguredGatewayBindings>(); return services.BuildServiceProvider();
    }
    private sealed class Secrets(bool same, bool rotate) : IHarnessSecrets
    {
        public string Resolve(string reference) => new(same ? 'S' : reference switch {
            "env:TEST_DISPATCH" => rotate ? 'E' : 'D', "env:TEST_CALLBACK" => rotate ? 'B' : 'C',
            "env:TEST_NATIVE" => rotate ? 'M' : 'N', _ => throw new InvalidOperationException()
        }, 40);
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "GatewayBindingTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
