using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fanasa.AccessManagement.Web.Security;

public sealed class OrganizationHrOptions
{
    public bool Enabled { get; init; }
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string? ClientId { get; init; }
    public string? ServiceSubject { get; init; }
    public string? ReadScope { get; init; }
    public string? WriteScope { get; init; }
    public string? GatewayCertificateSha256 { get; init; }
    public bool IsConfigured => Enabled
        && Uri.TryCreate(Authority, UriKind.Absolute, out var issuer) && issuer.Scheme == "https"
        && issuer.AbsolutePath.TrimEnd('/') == "/realms/fanasa" && string.IsNullOrEmpty(issuer.UserInfo)
        && string.IsNullOrEmpty(issuer.Query) && string.IsNullOrEmpty(issuer.Fragment)
        && !string.IsNullOrWhiteSpace(Audience) && !string.IsNullOrWhiteSpace(ClientId)
        && Guid.TryParseExact(ServiceSubject, "D", out var subject) && subject != Guid.Empty && subject.ToString("D") == ServiceSubject
        && ValidScope(ReadScope) && ValidScope(WriteScope) && ReadScope != WriteScope
        && GatewayCertificateSha256 is { Length: 64 } && GatewayCertificateSha256.All(Uri.IsHexDigit);
    private static bool ValidScope(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace);
}

public static class OrganizationHrSecurity
{
    public const string Scheme = "OrganizationHrBearer";
    public static IServiceCollection AddOrganizationHrSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Organization:HrApi").Get<OrganizationHrOptions>() ?? new();
        services.AddSingleton(settings); services.AddSingleton<OrganizationHrBoundary>();
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false; options.RequireHttpsMetadata = true; options.SaveToken = false;
            if (settings.IsConfigured) options.Authority = settings.Authority;
            options.Audience = settings.Audience;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = settings.Authority, ValidateAudience = true, ValidAudience = settings.Audience,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero, ValidTypes = ["JWT", "at+jwt"], NameClaimType = "sub"
            };
            if (configuration["Platform:CaFile"] is string caFile)
            {
                options.BackchannelHttpHandler = PlatformEndpointTls.CreateHandler(caFile);
            }
            options.Events.OnTokenValidated = context =>
            {
                if (!settings.IsConfigured || !CanonicalPayload(context.SecurityToken)) context.Fail("Invalid HR service token.");
                return Task.CompletedTask;
            };
        });
        services.AddAuthorization(); return services;
    }
    private static bool CanonicalPayload(SecurityToken token)
    {
        var payload = token switch { JsonWebToken jwt => jwt.EncodedPayload, JwtSecurityToken jwt => jwt.RawPayload, _ => null };
        if (payload is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateObject()) if (!names.Add(item.Name)) return false;
            foreach (var name in new[] { "sub", "azp", "scope", "iss" })
                if (!document.RootElement.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
            if (document.RootElement.TryGetProperty("client_id", out var clientId) && clientId.ValueKind != JsonValueKind.String) return false;
            if (!document.RootElement.TryGetProperty("exp", out var expiry) || !NumericDate(expiry)) return false;
            return !document.RootElement.TryGetProperty("nbf", out var start) || NumericDate(start);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return false; }
    }
    private static bool NumericDate(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
        && seconds >= 0 && seconds.ToString(CultureInfo.InvariantCulture) == value.GetRawText();
}

public sealed class OrganizationHrBoundary(OrganizationHrOptions settings, PersistentAccessManagement access, PlatformRegistry registry)
{
    public async ValueTask<bool> IsGatewayAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured || !http.Request.IsHttps) return false;
        await http.Connection.GetClientCertificateAsync(cancellationToken);
        return IsGatewayCurrent(http);
    }
    public bool IsGatewayCurrent(HttpContext http) => settings.IsConfigured && http.Request.IsHttps
        && http.Connection.ClientCertificate is { } certificate
        && OrganizationHrHosting.IsPinnedGatewayCertificate(certificate, Convert.FromHexString(settings.GatewayCertificateSha256!));

    public bool Allows(ClaimsPrincipal principal, Guid tenant, bool write)
    {
        if (!settings.IsConfigured || principal.Identity?.IsAuthenticated != true || tenant == Guid.Empty) return false;
        var subjects = principal.FindAll("sub").Select(x => x.Value).ToArray();
        var clients = principal.FindAll("azp").Select(x => x.Value).ToArray();
        var scopes = principal.FindAll("scope").Select(x => x.Value).ToArray();
        var scope = write ? settings.WriteScope! : settings.ReadScope!;
        if (subjects.Length != 1 || subjects[0] != settings.ServiceSubject || clients.Length != 1 || clients[0] != settings.ClientId
            || principal.FindAll("client_id").Any(x => x.Value != settings.ClientId)
            || principal.FindAll("active").Any(x => !x.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
            || scopes.Length != 1 || !scopes[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal)
            || !Live(principal)) return false;
        var product = registry.Products().SingleOrDefault(x => x.Key == "organization-fabric");
        if (product is null || product.Audience != settings.Audience
            || !access.GetClients(product.Id).Any(x => x.Key == settings.ClientId && x.IsActive && x.AllowedScopes.Contains(scope, StringComparer.Ordinal))
            || !access.GetTenants().Any(x => x.Id == tenant && x.IsActive)
            || !access.GetUsers(tenant).Any(x => x.KeycloakSubject == settings.ServiceSubject && x.IsActive)) return false;
        var grants = access.GetGrants(settings.ServiceSubject!);
        var now = DateTimeOffset.UtcNow;
        return grants.Any(x => x.TenantId == tenant
            && x.Permission == (write ? "organization.write" : "organization.read") && (!x.ExpiresAt.HasValue || x.ExpiresAt > now)) && Live(principal);
    }
    public bool CanAppoint(Guid tenant, string? subject) => subject != settings.ServiceSubject
        && Guid.TryParseExact(subject, "D", out var id) && id != Guid.Empty && id.ToString("D") == subject
        && access.GetUsers(tenant).Any(x => x.KeycloakSubject == subject && x.IsActive);
    private static bool Live(ClaimsPrincipal user)
    {
        static bool Number(Claim[] claims, out long number)
        {
            number = 0; return claims.Length == 1 && long.TryParse(claims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                && number >= 0 && number.ToString(CultureInfo.InvariantCulture) == claims[0].Value;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var start = user.FindAll("nbf").ToArray();
        return Number(user.FindAll("exp").ToArray(), out var expiry) && now < expiry
            && (start.Length == 0 || Number(start, out var nbf) && now >= nbf);
    }
}

