using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Fanasa.AccessManagement.Web.Security;

public sealed class OrganizationBearerOptions
{
    public bool Enabled { get; init; }
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string? ClientId { get; init; }
    public string? Scope { get; init; }
    public bool IsConfigured => Enabled
        && Uri.TryCreate(Authority, UriKind.Absolute, out var issuer) && issuer.Scheme == "https"
        && issuer.AbsolutePath.TrimEnd('/') == "/realms/fanasa" && string.IsNullOrEmpty(issuer.UserInfo)
        && string.IsNullOrEmpty(issuer.Query) && string.IsNullOrEmpty(issuer.Fragment)
        && !string.IsNullOrWhiteSpace(Audience) && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(Scope) && !Scope.Any(char.IsWhiteSpace);
}

/// <summary>Bearer-only unit.save PEP. Existing browser cookie/OIDC policies are unchanged.</summary>
public static class OrganizationBearerSecurity
{
    public const string Scheme = "OrganizationBearer";
    public const string Policy = "OrganizationUnitSave";
    public const string Permission = "organization.write";

    public static IServiceCollection AddOrganizationBearerSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Organization:Bearer").Get<OrganizationBearerOptions>() ?? new();
        services.AddSingleton(settings);
        services.TryAddSingleton<IOrganizationUnitSaveAdmission, UnconfiguredOrganizationUnitSaveAdmission>();
        services.AddSingleton<OrganizationBearerBoundary>();
        services.AddSingleton<IAuthorizationHandler, OrganizationUnitSaveAuthorizationHandler>();
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.SaveToken = false;
            if (settings.IsConfigured) options.Authority = settings.Authority;
            options.Audience = settings.Audience;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = settings.Authority,
                ValidateAudience = true, ValidAudience = settings.Audience,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
                ValidTypes = ["JWT", "at+jwt"], NameClaimType = "sub"
            };
            options.Events.OnTokenValidated = context =>
            {
                // The library can normalize string/duplicate timing values while creating claims.
                // Inspect only the payload of the token whose signature was already validated.
                if (!HasCanonicalTiming(context.SecurityToken)) context.Fail("Invalid token timing claims.");
                return Task.CompletedTask;
            };
        });
        services.AddAuthorization(options => options.AddPolicy(Policy, policy =>
            policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().AddRequirements(new OrganizationUnitSaveRequirement())));
        return services;
    }

    private static bool HasCanonicalTiming(SecurityToken token)
    {
        var payload = token switch { JsonWebToken jwt => jwt.EncodedPayload, JwtSecurityToken jwt => jwt.RawPayload, _ => null };
        if (payload is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var expiry = document.RootElement.EnumerateObject().Where(x => x.Name == "exp").ToArray();
            var start = document.RootElement.EnumerateObject().Where(x => x.Name == "nbf").ToArray();
            static bool CanonicalInteger(JsonElement value) => value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var seconds) && seconds >= 0
                && seconds.ToString(CultureInfo.InvariantCulture) == value.GetRawText();
            return expiry.Length == 1 && CanonicalInteger(expiry[0].Value)
                && (start.Length == 0 || start.Length == 1 && CanonicalInteger(start[0].Value));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return false; }
    }
}

/// <summary>Reads authoritative records on every invocation, including retries; token permission claims are never grants.</summary>
public sealed class OrganizationBearerBoundary(OrganizationBearerOptions settings, PersistentAccessManagement access,
    PlatformRegistry registry, OrganizationUsageMetering metering, IOrganizationUnitSaveAdmission admission)
{
    public bool Allows(ClaimsPrincipal principal, Guid tenant)
    {
        if (!settings.IsConfigured || !metering.Enabled || !admission.IsConfigured
            || principal.Identity?.IsAuthenticated != true || tenant == Guid.Empty) return false;
        // JwtBearer validates at authentication time. Admission can await past that deadline,
        // so every fresh boundary check also evaluates the signed timing claims against now.
        var now = DateTimeOffset.UtcNow;
        var notBefore = principal.FindAll("nbf").ToArray();
        long start = 0;
        if (!TryNumericDate(principal.FindAll("exp"), out var expiry) || now.ToUnixTimeSeconds() >= expiry
            || (notBefore.Length != 0 && (!TryNumericDate(notBefore, out start) || now.ToUnixTimeSeconds() < start))) return false;
        var subjects = principal.FindAll("sub").Select(x => x.Value).ToArray();
        var clients = principal.FindAll("azp").Select(x => x.Value).ToArray();
        if (subjects.Length != 1 || !Guid.TryParseExact(subjects[0], "D", out var subject) || subject == Guid.Empty
            || clients.Length != 1 || clients[0] != settings.ClientId
            || principal.FindAll("client_id").Any(x => x.Value != settings.ClientId)
            || principal.FindAll("active").Any(x => x.Value.Equals("false", StringComparison.OrdinalIgnoreCase))
            || !principal.FindAll("scope").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(settings.Scope, StringComparer.Ordinal)) return false;
        var product = registry.Products().SingleOrDefault(x => x.Key == OrganizationUsageMetering.ProductKey);
        if (product is null || product.Audience != settings.Audience
            || !access.GetClients(product.Id).Any(x => x.Key == settings.ClientId && x.IsActive
                && x.AllowedScopes.Contains(settings.Scope, StringComparer.Ordinal))) return false;
        if (!access.GetTenants().Any(x => x.Id == tenant && x.IsActive)
            || !access.GetUsers(tenant).Any(x => x.IsActive && x.KeycloakSubject == subjects[0])) return false;
        var grants = access.GetGrants(subjects[0]);
        // Evaluate again after record reads, immediately before returning an allow.
        now = DateTimeOffset.UtcNow;
        return now.ToUnixTimeSeconds() < expiry && now.ToUnixTimeSeconds() >= start
            && grants.Any(x => x.TenantId == tenant && x.Permission == OrganizationBearerSecurity.Permission
                && (!x.ExpiresAt.HasValue || x.ExpiresAt > now));
    }

    private static bool TryNumericDate(IEnumerable<Claim> claims, out long value)
    {
        value = 0;
        var values = claims.Select(x => x.Value).ToArray();
        return values.Length == 1 && long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0 && value.ToString(CultureInfo.InvariantCulture) == values[0];
    }
}

internal sealed class OrganizationUnitSaveRequirement : IAuthorizationRequirement;
internal sealed class OrganizationUnitSaveAuthorizationHandler(OrganizationBearerBoundary boundary)
    : AuthorizationHandler<OrganizationUnitSaveRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OrganizationUnitSaveRequirement requirement)
    {
        var http = context.Resource as HttpContext ?? (context.Resource as AuthorizationFilterContext)?.HttpContext;
        if (http is not null && Guid.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var tenant)
            && boundary.Allows(context.User, tenant)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
