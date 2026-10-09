using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fanasa.AgentGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class SqlGatewayJournalTests
{
    [Fact]
    public async Task Http_ingress_accepts_only_durable_scoped_idempotent_requests_and_never_executes()
    {
        var f = await Fixture.Create(); await using var host = await Ingress.Create(f);
        var body = host.Body;
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(body, bearer: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(body, bearer: new string('C', 40))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Send(body, id: Guid.NewGuid().ToString("N"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await host.Send(body, type: "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Send(body.Replace(f.Scope.ProjectId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Send(body.Replace("https://callback.invalid/", "https://untrusted.invalid/"))).StatusCode);
        await using (var db = f.Factory.CreateDbContext()) { Assert.Empty(await db.Runs.ToArrayAsync(Ct)); }
        Assert.Equal(HttpStatusCode.Accepted, (await host.Send(body)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await host.Send(body)).StatusCode);
        var conflict = await host.Send(body + " "); Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var response = await conflict.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(new string('D', 40), response); Assert.DoesNotContain(body, response);
        await using var final = f.Factory.CreateDbContext();
        Assert.Single(await final.Runs.ToArrayAsync(Ct)); Assert.Single(await final.Activations.ToArrayAsync(Ct));
        Assert.Single(await final.OutboxMessages.ToArrayAsync(Ct));
        Assert.Equal(0, f.Adapter.Prepared); Assert.Equal(0, f.Adapter.Submitted);
        Assert.Empty(f.Delivery.Bodies);
    }

    [Fact]
    public async Task Http_ingress_bounds_chunked_body_rejects_invalid_utf8_and_compression()
    {
        var f = await Fixture.Create(); await using var host = await Ingress.Create(f);
        using var oversized = host.Request(new StreamingBody(new byte[GatewayEndpoints.MaxBodyBytes + 1]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.SendAsync(oversized, Ct)).StatusCode);
        using var invalid = host.Request(new ByteArrayContent([0xff, 0xfe]));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(invalid, Ct)).StatusCode);
        using var compressed = host.Request(new StringContent(host.Body));
        compressed.Content!.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await host.Client.SendAsync(compressed, Ct)).StatusCode);
        using var duplicatedKey = host.Request(new StringContent(host.Body));
        duplicatedKey.Headers.Add("Idempotency-Key", f.Scope.RunId.ToString("N"));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(duplicatedKey, Ct)).StatusCode);
        await using var db = f.Factory.CreateDbContext(); Assert.Empty(await db.Runs.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Production_ingress_refuses_cleartext_even_with_valid_authentication()
    {
        var f = await Fixture.Create(); await using var host = await Ingress.Create(f, "Production");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Send(host.Body)).StatusCode);
        await using var db = f.Factory.CreateDbContext(); Assert.Empty(await db.Runs.ToArrayAsync(Ct));
    }

    private sealed class Ingress(WebApplication app, Fixture fixture, string body) : IAsyncDisposable
    {
        public string Body => body;
        public HttpClient Client { get; } = new(new HttpClientHandler { AllowAutoRedirect = false });
        public HttpRequestMessage Request(HttpContent content)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, app.Urls.Single() + "/bindings/coding/runs") { Content = content };
            request.Headers.Authorization = new("Bearer", new string('D', 40));
            request.Headers.Add("Idempotency-Key", fixture.Scope.RunId.ToString("N"));
            content.Headers.ContentType = new("application/json"); return request;
        }
        public async Task<HttpResponseMessage> Send(string value, string? bearer = "default", string? id = null, string type = "application/json")
        {
            using var request = Request(new StringContent(value, Encoding.UTF8, type));
            request.Content!.Headers.ContentType = new(type);
            request.Headers.Authorization = bearer is null ? null : new("Bearer", bearer == "default" ? new string('D', 40) : bearer);
            if (id is not null) { request.Headers.Remove("Idempotency-Key"); request.Headers.Add("Idempotency-Key", id); }
            return await Client.SendAsync(request, Ct);
        }
        public static async Task<Ingress> Create(Fixture f, string environment = "Testing")
        {
            var callback = $"https://callback.invalid/api/orchestration/v1/organizations/{f.Scope.OrganizationId:D}/workspaces/{f.Scope.WorkspaceId:D}/harness/coding/runs/{f.Scope.RunId:D}/result";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["AgentGateway:Enabled"] = "true", ["AgentGateway:AllowLoopbackHttp"] = "true",
                ["AgentGateway:Bindings:coding:Enabled"] = "true",
                ["AgentGateway:Bindings:coding:AgentProfileId"] = f.Bindings.Profile.ToString(),
                ["AgentGateway:Bindings:coding:SandboxId"] = "test-sandbox",
                ["AgentGateway:Bindings:coding:Revision"] = new('a', 40),
                ["AgentGateway:Bindings:coding:RepositoryUrl"] = "https://repository.invalid/project",
                ["AgentGateway:Bindings:coding:ImageDigest"] = "sha256:" + new string('a', 64),
                ["AgentGateway:Bindings:coding:CallbackBaseUrl"] = "https://callback.invalid/",
                ["AgentGateway:Bindings:coding:DispatchSecretRef"] = "env:TEST_DISPATCH",
                ["AgentGateway:Bindings:coding:CallbackSecretRef"] = "env:TEST_CALLBACK",
                ["NativeAgents:Enabled"] = "true", ["NativeAgents:Connections:coding:Enabled"] = "true",
                ["NativeAgents:Connections:coding:Engine"] = "opencode",
                ["NativeAgents:Connections:coding:OrganizationId"] = f.Scope.OrganizationId.ToString(),
                ["NativeAgents:Connections:coding:WorkspaceId"] = f.Scope.WorkspaceId.ToString(),
                ["NativeAgents:Connections:coding:ProjectId"] = f.Scope.ProjectId.ToString(),
                ["NativeAgents:Connections:coding:Endpoint"] = "https://native.invalid/",
                ["NativeAgents:Connections:coding:SecretRef"] = "env:TEST_NATIVE",
                ["NativeAgents:Connections:coding:ModelProvider"] = "fixture-provider",
                ["NativeAgents:Connections:coding:ModelId"] = "fixture-model"
            });
            builder.Services.AddSingleton<IHarnessSecrets, IngressSecrets>(); builder.Services.AddNativeAgentAdapters();
            builder.Services.AddSingleton<ConfiguredGatewayBindings>();
            builder.Services.AddSingleton<IGatewayBindings>(sp => sp.GetRequiredService<ConfiguredGatewayBindings>());
            builder.Services.AddSingleton<IGatewayJournal>(f.Journal);
            builder.Services.AddSingleton<IGatewaySandbox>(f.Sandbox); builder.Services.AddSingleton<IGatewayResultDelivery>(f.Delivery);
            builder.Services.AddSingleton<TimeProvider>(f.Clock); builder.Services.AddScoped<GatewayExecution>();
            var app = builder.Build(); app.MapGatewayEndpoints(); await app.StartAsync(Ct);
            return new(app, f, f.Body().Replace("https://callback.invalid/result", callback));
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(Ct); await app.DisposeAsync(); }
    }
    private sealed class IngressSecrets : IHarnessSecrets
    {
        public string Resolve(string reference) => new(reference switch {
            "env:TEST_DISPATCH" => 'D', "env:TEST_CALLBACK" => 'C', "env:TEST_NATIVE" => 'N', _ => throw new InvalidOperationException()
        }, 40);
    }
    private sealed class StreamingBody(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
