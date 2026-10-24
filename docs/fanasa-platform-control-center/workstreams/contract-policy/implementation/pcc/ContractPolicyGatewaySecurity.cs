using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

/// <summary>Explicit delegation to a registered Gateway service; omission keeps the endpoint closed.</summary>
public sealed record ContractGatewayDecisionOptions
{
    public bool Enabled { get; init; }
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string? ClientId { get; init; }
    public string? Scope { get; init; }
    public string? ProductKey { get; init; }
    internal string? HostAuthority { get; init; }
    internal string? HumanClientId { get; init; }
    internal string? ProducerClientId { get; init; }

    public bool IsConfigured => Enabled && IsCanonicalAuthority(Authority)
        && string.Equals(Authority, HostAuthority, StringComparison.Ordinal)
        && string.Equals(Audience, "fanasa-access-management-web", StringComparison.Ordinal)
        && IsIdentifier(ClientId) && IsIdentifier(ProductKey)
        && !string.Equals(ClientId, HumanClientId, StringComparison.Ordinal)
        && !string.Equals(ClientId, ProducerClientId, StringComparison.Ordinal)
        && Scope is { Length: > 0 and <= 1024 }
        && !Scope.Any(c => c < '!' || c > '~' || c is '"' or '\\');

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 1024 }
        && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool IsCanonicalAuthority(string? value) => value is { Length: > 0 and <= 1024 }
        && Uri.TryCreate(value, UriKind.Absolute, out var issuer)
        && issuer.Scheme == Uri.UriSchemeHttps && issuer.AbsolutePath == "/realms/fanasa"
        && issuer.UserInfo.Length == 0 && issuer.Query.Length == 0 && issuer.Fragment.Length == 0
        && string.Equals(value, issuer.AbsoluteUri, StringComparison.Ordinal);
}

public static class ContractGatewayDecisionServices
{
    public static IServiceCollection AddContractGatewayDecisionServices(this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = (configuration.GetSection("ContractPolicy:GatewayDecision").Get<ContractGatewayDecisionOptions>() ?? new())
            with
            {
                HostAuthority = configuration["Authentication:Authority"],
                HumanClientId = configuration["Organization:Bearer:ClientId"],
                ProducerClientId = configuration["ContractPolicy:Producer:ClientId"]
            };
        services.AddSingleton(settings);
        services.AddSingleton<ContractGatewayDecisionAuthorization>();
        services.AddSingleton<ContractGatewayDecisionService>();
        services.AddAuthentication().AddJwtBearer(ContractGatewayDecisionAuthentication.Scheme, options =>
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
            options.Events.OnMessageReceived = context =>
            {
                if (!settings.IsConfigured) context.NoResult();
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = context =>
            {
                if (!ContractGatewayDecisionAuthentication.HasCanonicalPayload(context.SecurityToken, settings))
                    context.Fail("Invalid Gateway token claims.");
                return Task.CompletedTask;
            };
        });
        return services;
    }
}

/// <summary>Validates raw payload only after the standard JWT handler verified its signature.</summary>
public static class ContractGatewayDecisionAuthentication
{
    public const string Scheme = "GatewayDecisionBearer";

    internal static bool HasCanonicalPayload(SecurityToken token, ContractGatewayDecisionOptions settings)
    {
        var payload = token switch
        {
            JsonWebToken jwt => jwt.EncodedPayload,
            JwtSecurityToken jwt => jwt.RawPayload,
            _ => null
        };
        if (payload is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !names.Add(property.Name))) return false;
            static bool StringEquals(JsonElement root, string name, string? expected) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
            if (!StringEquals(root, "iss", settings.Authority) || !StringEquals(root, "azp", settings.ClientId)
                || !root.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String
                || subject.GetString() is not { Length: > 0 and <= 1024 } subjectText || subjectText.Any(char.IsControl)
                || !root.TryGetProperty("scope", out var scopeValue) || scopeValue.ValueKind != JsonValueKind.String)
                return false;
            var scope = scopeValue.GetString();
            if (scope is null || scope.Any(c => c != ' ' && (c < '!' || c > '~' || c is '"' or '\\'))
                || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(settings.Scope, StringComparer.Ordinal))
                return false;
            if (root.TryGetProperty("client_id", out _) && !StringEquals(root, "client_id", settings.ClientId)) return false;
            if (root.TryGetProperty("active", out var active) && active.ValueKind != JsonValueKind.True) return false;
            if (!root.TryGetProperty("aud", out var audience)) return false;
            if (audience.ValueKind == JsonValueKind.String)
            {
                if (!string.Equals(audience.GetString(), settings.Audience, StringComparison.Ordinal)) return false;
            }
            else if (audience.ValueKind == JsonValueKind.Array)
            {
                var values = audience.EnumerateArray().ToArray();
                var unique = new HashSet<string>(StringComparer.Ordinal);
                if (values.Length == 0 || values.Any(value => value.ValueKind != JsonValueKind.String
                        || string.IsNullOrEmpty(value.GetString()) || !unique.Add(value.GetString()!))
                    || !values.Any(value => string.Equals(value.GetString(), settings.Audience, StringComparison.Ordinal)))
                    return false;
            }
            else return false;
            static bool CanonicalInteger(JsonElement value, long minimum) => value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var seconds) && seconds >= minimum && seconds <= 253_402_300_799
                && string.Equals(seconds.ToString(CultureInfo.InvariantCulture), value.GetRawText(), StringComparison.Ordinal);
            return root.TryGetProperty("exp", out var expiry) && CanonicalInteger(expiry, 1)
                && (!root.TryGetProperty("nbf", out var start) || CanonicalInteger(start, 0));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>The service client and its scope remain current at every request boundary.</summary>
public sealed class ContractGatewayDecisionAuthorization(ContractGatewayDecisionOptions options,
    PersistentAccessManagement access, PlatformRegistry registry, TimeProvider clock)
{
    public bool Allows(ClaimsPrincipal principal)
    {
        if (!options.IsConfigured
            || !ContractTokenIdentity.TryReadIdentityAndTiming(principal, clock, out var identity, out var expires)) return false;
        static string? Single(ClaimsIdentity identity, string type)
        {
            var claims = identity.Claims.Where(claim => string.Equals(claim.Type, type, StringComparison.Ordinal)).Take(2).ToArray();
            return claims.Length == 1 ? claims[0].Value : null;
        }
        if (!string.Equals(Single(identity, "iss"), options.Authority, StringComparison.Ordinal)
            || !string.Equals(Single(identity, "azp"), options.ClientId, StringComparison.Ordinal)
            || Single(identity, "sub") is not { Length: > 0 and <= 1024 } subject || subject.Any(char.IsControl)
            || !identity.Claims.Any(claim => claim.Type == "aud" && claim.Value == options.Audience)
            || identity.Claims.Any(claim => claim.Type == "client_id" && claim.Value != options.ClientId)
            || identity.Claims.Any(claim => claim.Type == "active" && !claim.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
            return false;
        var scope = Single(identity, "scope");
        if (scope is null || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(options.Scope, StringComparer.Ordinal))
            return false;
        try
        {
            var products = registry.Products().Where(product => product.Key == options.ProductKey).Take(2).ToArray();
            if (products.Length != 1 || products[0].TenantId is not { } owner || owner == Guid.Empty
                || access.GetTenants().Count(tenant => tenant.Id == owner && tenant.IsActive) != 1) return false;
            // The sender's product owns this client; its own API audience can differ from the receiving PCC audience.
            var clients = access.GetClients(products[0].Id).Where(client => client.Key == options.ClientId).Take(2).ToArray();
            if (clients.Length != 1 || !clients[0].IsActive
                || !clients[0].AllowedScopes.Contains(options.Scope, StringComparer.Ordinal)) return false;
            return ContractTokenIdentity.TryReadIdentityAndTiming(principal, clock, out var currentIdentity, out var currentExpires)
                && ReferenceEquals(identity, currentIdentity) && expires == currentExpires;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
