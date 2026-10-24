using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Neo.Infrastructure.Features.Client.Keycloak;
using Xunit;

namespace Neo.Infrastructure.IntegrationTests.Features.Client;

public sealed class KeycloakCredentialBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("09120000000")]
    [InlineData("another-user")]
    public async Task MobileAloneNeverIssuesTokenOrContactsIdentityProvider(string mobile)
    {
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("No HTTP expected"));
        using var client = new HttpClient(handler);
        var service = Create(client);

        var result = await service.GetUserTokenAsync(mobile, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancelledLegacyRequestDoesNotContactIdentityProvider()
    {
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("No HTTP expected"));
        using var client = new HttpClient(handler);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).GetUserTokenAsync("test-user", cancelled.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RegistrationDoesNotAssignAnyPasswordAndPreservesUserAndRoleRequests()
    {
        using var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/admin/realms/test/users" when request.Method == HttpMethod.Post => Reply("", HttpStatusCode.Created),
            "/admin/realms/test/users" => Reply("[{\"id\":\"user-1\",\"username\":\"test-mobile\"}]"),
            "/admin/realms/test/clients" => Reply("[{\"id\":\"client-1\",\"clientId\":\"neo-club\"}]"),
            "/admin/realms/test/users/user-1/role-mappings/clients/client-1" => Reply("[]"),
            "/admin/realms/test/roles/test-role" => Reply("{\"id\":\"role-1\",\"name\":\"test-role\"}"),
            "/admin/realms/test/users/user-1/role-mappings/realm" when request.Method == HttpMethod.Post => Reply(""),
            _ => throw new InvalidOperationException("Unexpected synthetic route")
        });
        using var client = new HttpClient(handler);

        var result = await Create(client).AddUserAsync("app-user-1", "test-mobile", "test-role");

        Assert.Equal("user-1", result.id);
        Assert.Equal(7, handler.Requests.Count);
        var create = handler.Requests[0];
        using var payload = JsonDocument.Parse(create.Body);
        Assert.Equal("test-mobile", payload.RootElement.GetProperty("username").GetString());
        Assert.Equal("app-user-1", payload.RootElement.GetProperty("attributes").GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Array, payload.RootElement.GetProperty("credentials").ValueKind);
        Assert.Equal(0, payload.RootElement.GetProperty("credentials").GetArrayLength());
        Assert.DoesNotContain("password", create.Body, StringComparison.OrdinalIgnoreCase);
        Assert.All(handler.Requests, request => Assert.Equal("idp.invalid", request.Uri.Host));
        Assert.Equal(HttpMethod.Post, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task FailedRegistrationDoesNotFallBackToSharedCredentialOrFetchUser()
    {
        using var handler = new RecordingHandler(_ => Reply("", HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(client).AddUserAsync("user-1", "test-mobile", "test-role"));

        Assert.Single(handler.Requests);
        using var payload = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(0, payload.RootElement.GetProperty("credentials").GetArrayLength());
    }

    [Fact]
    public async Task ExplicitPasswordRequestRetainsOnlyCallerSuppliedSyntheticPassword()
    {
        using var handler = new RecordingHandler(_ => Reply("{}"));
        using var client = new HttpClient(handler);

        using var response = await Create(client).GetPasswordTokenAsync("test-user", "test-only-user-password");

        var request = Assert.Single(handler.Requests);
        var form = DecodeForm(request.Body);
        Assert.Equal("test-only-user-password", form["password"]);
        Assert.Equal("test-user", form["username"]);
        Assert.Equal("password", form["grant_type"]);
        Assert.Equal("test-client", form["client_id"]);
        Assert.Equal("test-only-client-secret", form["client_secret"]);
        Assert.Equal("/realms/test/protocol/openid-connect/token", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task RefreshRequestRetainsCallerTokenWithoutPasswordFallback()
    {
        using var handler = new RecordingHandler(_ => Reply("{\"access_token\":\"test-only-access-token\",\"refresh_token\":\"test-only-new-refresh-token\"}"));
        using var client = new HttpClient(handler);

        var result = await Create(client).GetUserTokenByRefreshTokenAsync("test-only-refresh-token", TestContext.Current.CancellationToken);

        Assert.Equal("test-only-access-token", result!.access_token);
        var form = DecodeForm(Assert.Single(handler.Requests).Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("test-only-refresh-token", form["refresh_token"]);
        Assert.False(form.ContainsKey("password"));
        Assert.False(form.ContainsKey("username"));
    }

    private static KeycloakIdpService Create(HttpClient client)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdpSetting:AdminBaseUri"] = "https://idp.invalid/admin/realms/test",
            ["IdpSetting:TokenUri"] = "https://idp.invalid/realms/test/protocol/openid-connect/token",
            ["IdpSetting:ClientId"] = "test-client",
            ["IdpSetting:ClientSecret"] = "test-only-client-secret"
        }).Build();
        return new KeycloakIdpService(client, new ClientFactory(client), NullLogger<KeycloakIdpService>.Instance, configuration);
    }

    private static Dictionary<string, string> DecodeForm(string body) => body.Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private static HttpResponseMessage Reply(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record Request(HttpMethod Method, Uri Uri, string Body);
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class RecordingHandler(Func<Request, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            var request = new Request(message.Method, message.RequestUri!,
                message.Content is null ? "" : await message.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(request);
            return reply(request);
        }
    }
}
