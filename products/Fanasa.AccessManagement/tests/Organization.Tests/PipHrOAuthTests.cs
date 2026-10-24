using System.Net;
using System.Text;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.Extensions.Configuration;

static class PipHrOAuthTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-10-24T10:00:00Z"));
        var tenant = Guid.NewGuid(); var subject = Guid.NewGuid().ToString("D");
        var member = new TenantUser(Guid.NewGuid(), tenant, subject, "Synthetic", true, clock.Now.AddDays(-1));
        var unit = new OrgUnit(Guid.NewGuid(), "Synthetic unit", "unit", null, "department", true);
        var role = new OrgRole(Guid.NewGuid(), "Synthetic role", "reader");
        var position = new OrgPosition(Guid.NewGuid(), unit.Id, "Synthetic position", "position", 1, true, role.Id);
        var appointment = new OrgAppointment(Guid.NewGuid(), position.Id, subject, clock.Now, clock.Now.AddSeconds(20));
        var chart = new OrgState(tenant, 7, [unit], [position], [appointment], [], [role]);
        var projector = new OrganizationHrAttributeProjector(clock, new());
        var first = projector.Project(chart, member);
        check(first.SchemaVersion == 2 && first.ObservedAt == clock.Now && first.ValidUntil == appointment.To,
            "HR observations expire at the known appointment end, earlier than observation TTL");
        check(first.Attributes["org.assignment.active"] == "true" && first.Attributes["org.assignment.count"] == "1"
            && first.Attributes["org.member.active"] == "true" && first.Attributes["org.role.code"] == "reader", "HR start boundary is inclusive and projection contains real membership/assignment facts");
        check(!first.Attributes.Keys.Any(x => x.StartsWith("employment.", StringComparison.Ordinal)), "HR membership never claims legal employment");
        check(projector.Project(chart, member).SourceVersion != first.SourceVersion, "Every HR observation gets its own immutable source version");
        clock.Now = appointment.To!.Value;
        var ended = projector.Project(chart, member);
        check(ended.Attributes["org.assignment.active"] == "false" && !ended.Attributes.ContainsKey("org.position.code"), "HR appointment end boundary is exclusive and removes scalar assignment claims");
        clock.Now = first.ObservedAt;
        var inactive = projector.Project(chart, member with { IsActive = false });
        check(inactive.Attributes["org.member.active"] == "false" && inactive.Attributes["org.assignment.count"] == "0", "Inactive HR membership is explicit and has no active assignment");
        var secondPosition = position with { Id = Guid.NewGuid(), Code = "second" };
        var multiple = projector.Project(chart with { Positions = [position, secondPosition], Appointments = [appointment, appointment with { Id = Guid.NewGuid(), PositionId = secondPosition.Id }] }, member);
        check(multiple.Attributes["org.assignment.count"] == "2" && !multiple.Attributes.ContainsKey("org.unit.code") && !multiple.Attributes.ContainsKey("org.role.code"), "Concurrent HR assignments do not select arbitrary unit, position or role claims");
        var future = projector.Project(chart with { Appointments = [appointment with { From = clock.Now.AddSeconds(10), To = null }] }, member);
        check(future.Attributes["org.assignment.active"] == "false" && future.ValidUntil == clock.Now.AddSeconds(10), "Inactive observation expires at the next future appointment start");
        Reject(() => projector.Project(chart with { Positions = [position with { Active = false }] }, member), "Inactive assignment position fails closed");
        Reject(() => projector.Project(chart with { Units = [unit with { Active = false }] }, member), "Inactive assignment unit fails closed");
        Reject(() => projector.Project(chart with { Roles = [] }, member), "Missing referenced role fails closed");
        Reject(() => projector.Project(chart, member with { TenantId = Guid.NewGuid() }), "HR projector rejects cross-tenant membership");

        var profile = new PipOAuthProfile { TenantId = tenant, Reference = "oauth-hr", ClientId = "synthetic-reader",
            ClientSecretReference = "hr-client", TokenEndpoint = "https://sso.example.test/token", Scopes = ["organization.hr.attributes.read"] };
        var options = new PipOAuthOptions { Profiles = [profile] }; options.Validate();
        var binding = new PipEndpointBinding { TenantId = tenant, Endpoint = "https://hr.example.test/attributes", SecretReference = profile.Reference };
        options.Validate(new() { Bindings = [binding] });
        var crossTenantRejected = false;
        try { options.Validate(new() { Bindings = [new() { TenantId = Guid.NewGuid(), Endpoint = binding.Endpoint, SecretReference = binding.SecretReference }] }); }
        catch (InvalidOperationException) { crossTenantRejected = true; }
        check(crossTenantRejected, "OAuth profile cannot be accidentally bound to a second tenant");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Pip:Secrets:hr-client"] = "synthetic-secret", ["Pip:Secrets:legacy"] = "static-bearer" }).Build();
        var handler = new TokenHandler(); using var client = new HttpClient(handler); var provider = new Provider(client);
        var resolver = new OAuthPipSecretResolver(clock, options, new(configuration), provider);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => resolver.ResolveAsync("oauth-hr").AsTask()));
        check(results.All(x => x == "synthetic-token-1") && handler.Calls == 1, "Concurrent OAuth lookups share one in-memory token renewal");
        check(handler.Form.Contains("grant_type=client_credentials", StringComparison.Ordinal) && handler.Form.Contains("scope=organization.hr.attributes.read", StringComparison.Ordinal), "OAuth uses client credentials and exact requested read scope");
        clock.Now = clock.Now.AddSeconds(54); _ = await resolver.ResolveAsync("oauth-hr");
        check(handler.Calls == 2, "OAuth proactively renews tokens before expiry");
        check(await resolver.ResolveAsync("legacy") == "static-bearer", "Existing explicit static credentials remain compatible");
        handler.Payload = "{\"access_token\":\"sensitive-secret\",\"token_type\":\"Bearer\",\"expires_in\":60,\"scope\":\"admin\"}";
        clock.Now = clock.Now.AddSeconds(60);
        await RejectAsync(() => resolver.ResolveAsync("oauth-hr").AsTask(), "OAuth rejects broadened response scope without exposing token content");
        handler.Payload = "{\"access_token\":\"sensitive-secret\",\"access_token\":\"second\",\"token_type\":\"Bearer\",\"expires_in\":60}";
        await RejectAsync(() => resolver.ResolveAsync("oauth-hr").AsTask(), "OAuth rejects duplicate token JSON fields");
        handler.Status = HttpStatusCode.Redirect;
        await RejectAsync(() => resolver.ResolveAsync("oauth-hr").AsTask(), "OAuth refuses redirect responses");
        var missing = new OAuthPipSecretResolver(clock, options, new(new ConfigurationBuilder().Build()), provider);
        await RejectAsync(() => missing.ResolveAsync("oauth-hr").AsTask(), "OAuth fails closed when the deployment secret is missing");
        void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { check(true, label); return; } throw new Exception(label); }
        async Task RejectAsync(Func<Task<string?>> action, string label)
        { try { await action(); } catch (InvalidOperationException error) { check(!error.ToString().Contains("sensitive-secret", StringComparison.Ordinal), label); return; } throw new Exception(label); }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Provider(HttpClient client) : IPipHttpClientProvider { public HttpClient GetClient(PipEndpointBinding binding) => client; }
    private sealed class TokenHandler : HttpMessageHandler
    {
        public int Calls; public string Form = ""; public string? Payload; public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); Form = await request.Content!.ReadAsStringAsync(cancellationToken);
            await Task.Yield();
            return new(Status) { Content = new StringContent(Payload ?? $"{{\"access_token\":\"synthetic-token-{Calls}\",\"token_type\":\"Bearer\",\"expires_in\":60,\"scope\":\"organization.hr.attributes.read\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
