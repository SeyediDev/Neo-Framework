using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.ContractPolicy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

/// <summary>Explicit host configuration; omission leaves producer ingress disabled.</summary>
public sealed record ContractProducerOptions
{
    public bool Enabled { get; init; }
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string? ClientId { get; init; }
    public string? Scope { get; init; }
    public string? ProductKey { get; init; }
    internal string? HostAuthority { get; init; }

    public bool IsConfigured => Enabled && IsCanonicalAuthority(Authority)
        && string.Equals(Authority, HostAuthority, StringComparison.Ordinal)
        && string.Equals(Audience, "fanasa-access-management-web", StringComparison.Ordinal)
        && IsIdentifier(ClientId) && IsIdentifier(ProductKey)
        && Scope is { Length: > 0 and <= 1024 }
        && !Scope.Any(c => c < '!' || c > '~' || c is '"' or '\\');

    public ContractProducerBinding? Binding => IsConfigured
        ? new(Authority!, Audience!, ClientId!, Scope!) : null;

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 1024 }
        && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool IsCanonicalAuthority(string? value) => value is { Length: > 0 and <= 1024 }
        && Uri.TryCreate(value, UriKind.Absolute, out var issuer)
        && issuer.Scheme == Uri.UriSchemeHttps && issuer.AbsolutePath == "/realms/fanasa"
        && issuer.UserInfo.Length == 0 && issuer.Query.Length == 0 && issuer.Fragment.Length == 0
        && string.Equals(value, issuer.AbsoluteUri, StringComparison.Ordinal);
}

/// <summary>Uses the central IdP through the existing ASP.NET JWT handler.</summary>
public static class ContractProducerSecurity
{
    public const string Scheme = "ContractProducerBearer";

    public static IServiceCollection AddContractProducerSecurity(this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = (configuration.GetSection("ContractPolicy:Producer").Get<ContractProducerOptions>() ?? new())
            with { HostAuthority = configuration["Authentication:Authority"] };
        services.AddSingleton(settings);
        services.AddSingleton<ContractProducerAuthorization>();
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
            options.Events.OnMessageReceived = context =>
            {
                if (!settings.IsConfigured) context.NoResult();
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = context =>
            {
                // Read only the raw payload of the token whose signature the handler validated.
                // Claim materialization can otherwise hide duplicate or coerced property values.
                if (!HasCanonicalPayload(context.SecurityToken, settings)) context.Fail("Invalid producer token claims.");
                return Task.CompletedTask;
            };
        });
        // The existing organization handler owns cryptographic and canonical timing
        // validation. Preserve that event and reject identity ambiguity before its
        // claims are used by the PIP; authorization keeps its existing 403 boundary.
        services.PostConfigure<JwtBearerOptions>(OrganizationBearerSecurity.Scheme, options =>
        {
            var original = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                await original(context);
                if (context.Result?.Failure is not null) return;
                if (!HasUnambiguousIdentity(context.SecurityToken)) context.Fail("Invalid token identity claims.");
            };
        });
        return services;
    }

    private static bool HasCanonicalPayload(SecurityToken token, ContractProducerOptions settings)
    {
        var payload = EncodedPayload(token);
        if (payload is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(x => !names.Add(x.Name))) return false;
            static bool StringEquals(JsonElement root, string name, string? expected) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
            if (!StringEquals(root, "iss", settings.Authority) || !StringEquals(root, "azp", settings.ClientId)
                || !root.TryGetProperty("scope", out var scopeValue) || scopeValue.ValueKind != JsonValueKind.String)
                return false;
            var scope = scopeValue.GetString();
            if (scope is null || scope.Any(c => c != ' ' && (c < '!' || c > '~' || c is '"' or '\\'))
                || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(settings.Scope, StringComparer.Ordinal))
                return false;
            if (root.TryGetProperty("client_id", out _) && !StringEquals(root, "client_id", settings.ClientId))
                return false;
            if (!root.TryGetProperty("aud", out var audience)) return false;
            if (audience.ValueKind == JsonValueKind.String)
            {
                if (!string.Equals(audience.GetString(), settings.Audience, StringComparison.Ordinal)) return false;
            }
            else if (audience.ValueKind == JsonValueKind.Array)
            {
                var values = audience.EnumerateArray().ToArray();
                if (values.Length == 0 || values.Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(x.GetString()))
                    || !values.Any(x => string.Equals(x.GetString(), settings.Audience, StringComparison.Ordinal))) return false;
            }
            else return false;
            static bool CanonicalInteger(JsonElement value) => value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var seconds) && seconds >= 0
                && string.Equals(seconds.ToString(CultureInfo.InvariantCulture), value.GetRawText(), StringComparison.Ordinal);
            return root.TryGetProperty("exp", out var expiry) && CanonicalInteger(expiry)
                && (!root.TryGetProperty("nbf", out var start) || CanonicalInteger(start));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasUnambiguousIdentity(SecurityToken token)
    {
        var payload = EncodedPayload(token);
        if (payload is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(x => !names.Add(x.Name))) return false;
            foreach (var name in new[] { "iss", "azp", "sub", "scope" })
                if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return false;
            if (root.TryGetProperty("client_id", out var client) && client.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("aud", out var audience)) return false;
            return audience.ValueKind == JsonValueKind.String
                || audience.ValueKind == JsonValueKind.Array && audience.GetArrayLength() > 0
                && audience.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(x.GetString()));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string? EncodedPayload(SecurityToken token) => token switch
    {
        JsonWebToken jwt => jwt.EncodedPayload,
        JwtSecurityToken jwt => jwt.RawPayload,
        _ => null
    };
}

/// <summary>Checks current producer registration on every call, including the final commit boundary.</summary>
public sealed class ContractProducerAuthorization(ContractProducerOptions options, PersistentAccessManagement access,
    PlatformRegistry registry, TimeProvider clock)
{
    public bool Allows(ClaimsPrincipal principal)
    {
        if (!options.IsConfigured
            || !ContractTokenIdentity.TryReadIdentityAndTiming(principal, clock, out var identity, out var expires))
            return false;
        static string? Single(ClaimsIdentity identity, string type)
        {
            var claims = identity.Claims.Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
                .Take(2).ToArray();
            return claims.Length == 1 ? claims[0].Value : null;
        }
        if (!string.Equals(Single(identity, "iss"), options.Authority, StringComparison.Ordinal)
            || !string.Equals(Single(identity, "azp"), options.ClientId, StringComparison.Ordinal)
            || !identity.Claims.Any(x => string.Equals(x.Type, "aud", StringComparison.Ordinal)
                && string.Equals(x.Value, options.Audience, StringComparison.Ordinal))
            || identity.Claims.Any(x => string.Equals(x.Type, "client_id", StringComparison.Ordinal)
                && !string.Equals(x.Value, options.ClientId, StringComparison.Ordinal))
            || identity.Claims.Any(x => string.Equals(x.Type, "active", StringComparison.Ordinal)
                && string.Equals(x.Value, "false", StringComparison.Ordinal))) return false;
        var scope = Single(identity, "scope");
        if (scope is null || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(options.Scope, StringComparer.Ordinal)) return false;

        // Product and client data come from existing authoritative stores, never event fields.
        var products = registry.Products().Where(x => string.Equals(x.Key, options.ProductKey, StringComparison.Ordinal))
            .Take(2).ToArray();
        // ProductKey owns the producer service client. Its API audience may differ from
        // the receiving PCC audience, which the JWT handler and adapter bind separately.
        if (products.Length != 1) return false;
        var clients = access.GetClients(products[0].Id)
            .Where(x => string.Equals(x.Key, options.ClientId, StringComparison.Ordinal)).Take(2).ToArray();
        if (clients.Length != 1 || !clients[0].IsActive
            || !clients[0].AllowedScopes.Contains(options.Scope, StringComparer.Ordinal)) return false;

        // Store reads can cross expiry or a clock adjustment; reread both signed timing bounds.
        return ContractTokenIdentity.TryReadIdentityAndTiming(principal, clock, out var currentIdentity, out var currentExpiry)
            && ReferenceEquals(identity, currentIdentity) && expires == currentExpiry;
    }
}

/// <summary>Reads raw claim names from the sole authenticated identity, ignoring Fabric's supplemental identity.</summary>
public static class ContractTokenIdentity
{
    public static bool TryRead(ClaimsPrincipal principal, TimeProvider clock, out ClaimsIdentity identity,
        out string subject, out DateTimeOffset expires)
    {
        subject = string.Empty;
        if (!TryReadIdentityAndTiming(principal, clock, out identity, out expires)) return false;
        var subjects = identity.Claims.Where(x => string.Equals(x.Type, "sub", StringComparison.Ordinal))
            .Take(2).ToArray();
        if (subjects.Length != 1 || !Guid.TryParseExact(subjects[0].Value, "D", out var parsed) || parsed == Guid.Empty)
            return false;
        subject = subjects[0].Value; // Preserve the durable Keycloak subject literal, including its casing.
        return true;
    }

    internal static bool TryReadIdentityAndTiming(ClaimsPrincipal? principal, TimeProvider clock,
        out ClaimsIdentity identity, out DateTimeOffset expires)
    {
        identity = null!;
        expires = default;
        if (principal is null) return false;
        var identities = principal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1) return false;
        var candidate = identities[0];
        var expiry = candidate.Claims.Where(x => string.Equals(x.Type, "exp", StringComparison.Ordinal)).Take(2).ToArray();
        var start = candidate.Claims.Where(x => string.Equals(x.Type, "nbf", StringComparison.Ordinal)).Take(2).ToArray();
        if (!TryNumericDate(expiry, out var deadline)
            || start.Length != 0 && !TryNumericDate(start, out _)) return false;
        var now = clock.GetUtcNow();
        if (now >= deadline || start.Length == 1 && (!TryNumericDate(start, out var begins) || now < begins)) return false;
        identity = candidate;
        expires = deadline;
        return true;
    }

    private static bool TryNumericDate(Claim[] claims, out DateTimeOffset value)
    {
        value = default;
        if (claims.Length != 1
            || !long.TryParse(claims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0
            || !string.Equals(seconds.ToString(CultureInfo.InvariantCulture), claims[0].Value, StringComparison.Ordinal))
            return false;
        try { value = DateTimeOffset.FromUnixTimeSeconds(seconds); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
