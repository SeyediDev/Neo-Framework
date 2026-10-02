using System.Security.Claims;
using Neo.FanasaIdentity.Application;
using Neo.FanasaIdentity.Contracts;
using Neo.FanasaIdentity.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFanasaIdentity(builder.Configuration);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "neo-fanasa-identity" }));
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/v1/identity/me", (ClaimsPrincipal principal) => Results.Ok(ToHuman(principal)))
    .RequireAuthorization();

app.MapPost("/v1/identity/register", async (ClaimsPrincipal principal, IdentityRegistrationRequest request, IIdentityRegistry registry, CancellationToken ct) =>
{
    var human = ToHuman(principal);
    return Results.Ok(await registry.RegisterAsync(human, request, ct));
}).RequireAuthorization();

app.Run();

static AuthenticatedHuman ToHuman(ClaimsPrincipal principal)
{
    var subject = principal.FindFirstValue(FanasaIdentityClaims.Subject)
        ?? throw new InvalidOperationException("The token has no subject.");
    var tenants = principal.FindAll(FanasaIdentityClaims.Tenant).Select(x => x.Value)
        .Concat(principal.FindAll(FanasaIdentityClaims.TenantList).SelectMany(x => x.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var roles = principal.FindAll(FanasaIdentityClaims.HumanRole).Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    return new AuthenticatedHuman(subject, principal.FindFirstValue(FanasaIdentityClaims.Email), principal.Identity?.Name,
        principal.FindFirstValue(FanasaIdentityClaims.Provider) ?? "fanasa", tenants, roles);
}

public partial class Program;
