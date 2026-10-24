using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Fanasa.UnifiedPortal.Web;

public sealed record ConsumerBillingOptions(bool Enabled, Uri? GatewayOrigin, string Audience, string Scope, string ClientId, string Authority)
{
    public bool IsConfigured => Enabled && GatewayOrigin is not null && GatewayOrigin.Scheme == "https"
        && GatewayOrigin.Host.Equals("api.fanasa.net.local", StringComparison.OrdinalIgnoreCase) && GatewayOrigin.Port == 443
        && GatewayOrigin.AbsolutePath == "/" && GatewayOrigin.UserInfo.Length == 0 && GatewayOrigin.Query.Length == 0 && GatewayOrigin.Fragment.Length == 0
        && Label(Audience) && Label(Scope)
        && !Scope.Any(char.IsWhiteSpace) && Label(ClientId) && Uri.TryCreate(Authority, UriKind.Absolute, out var issuer)
        && issuer.Scheme == "https" && issuer.UserInfo.Length == 0 && issuer.Query.Length == 0 && issuer.Fragment.Length == 0
        && issuer.AbsolutePath.TrimEnd('/') == "/realms/fanasa";

    public static ConsumerBillingOptions Read(IConfiguration configuration)
    {
        var origin = configuration["ConsumerBilling:GatewayOrigin"];
        Uri? gateway = Uri.TryCreate(origin, UriKind.Absolute, out var parsed) && parsed.Scheme == "https"
            && parsed.Host.Equals("api.fanasa.net.local", StringComparison.OrdinalIgnoreCase)
            && parsed.Port == 443 && parsed.AbsolutePath == "/" && parsed.UserInfo.Length == 0
            && parsed.Query.Length == 0 && parsed.Fragment.Length == 0 ? parsed : null;
        // Financial reads never silently fall back to the catalog's direct PCC origin.
        if (gateway is not null && Uri.TryCreate(configuration["PlatformControlCenter:BaseUrl"], UriKind.Absolute, out var pcc)
            && string.Equals(gateway.GetLeftPart(UriPartial.Authority), pcc.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            gateway = null;
        return new(configuration.GetValue<bool>("ConsumerBilling:Enabled"), gateway,
            configuration["ConsumerBilling:Audience"] ?? "", configuration["ConsumerBilling:Scope"] ?? "",
            configuration["Authentication:ClientId"] ?? "", configuration["Authentication:Authority"]?.TrimEnd('/') ?? "");
    }
    private static bool Label(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim()
        && value.Length <= 200 && !value.Any(char.IsControl);
}

// Only a random handle is written into the protected authentication ticket. Tokens
// remain in bounded process memory and require a fresh human login after expiry.
public sealed class ConsumerBillingSessionVault(TimeProvider clock)
{
    internal const string TicketHandle = ".fanasa.billing.session";
    private const int Capacity = 2048;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private sealed record Entry(string Subject, string Session, string Token, DateTimeOffset ExpiresAt);

    internal string? Capture(string subject, string session, string token, DateTimeOffset expiresAt)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var entry in entries) if (entry.Value.ExpiresAt <= now) entries.TryRemove(entry.Key, out _);
            if (entries.Count >= Capacity || expiresAt <= now) return null;
            var expiry = expiresAt < now.AddMinutes(5) ? expiresAt : now.AddMinutes(5);
            var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            return entries.TryAdd(handle, new(subject, session, token, expiry)) ? handle : null;
        }
    }
    internal ConsumerUsageHumanCredential? Read(string? handle, string subject, string session)
    {
        if (handle is null || !entries.TryGetValue(handle, out var entry)) return null;
        if (entry.ExpiresAt <= clock.GetUtcNow()) { entries.TryRemove(handle, out _); return null; }
        return entry.Subject == subject && entry.Session == session
            ? new(entry.Subject, entry.Token, entry.ExpiresAt) : null;
    }
    public void Remove(AuthenticationProperties? properties)
    {
        if (properties?.Items.Remove(TicketHandle, out var handle) == true && handle is not null)
            entries.TryRemove(handle, out _);
    }
}

public sealed class SessionConsumerUsageCredentialProvider(IHttpContextAccessor accessor,
    ConsumerBillingOptions options, ConsumerBillingSessionVault vault) : IConsumerUsageHumanCredentialProvider
{
    public async ValueTask<ConsumerUsageHumanCredential?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = accessor.HttpContext;
        if (!options.IsConfigured || context is null) return null;
        var ticket = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ticket.Succeeded || ticket.Principal?.Identity?.IsAuthenticated != true
            || !ConsumerBillingOidc.CanonicalIdentity(ticket.Principal, out var subject, out var session)
            || !ConsumerBillingOidc.CanonicalIdentity(context.User, out var currentSubject, out var currentSession)
            || currentSubject != subject || currentSession != session) return null;
        string? handle = null;
        ticket.Properties?.Items.TryGetValue(ConsumerBillingSessionVault.TicketHandle, out handle);
        return vault.Read(handle, subject, session);
    }
}

public static class ConsumerBillingOidc
{
    public static async Task PurgeAsync(CookieSigningOutContext context)
    {
        var vault = context.HttpContext.RequestServices.GetRequiredService<ConsumerBillingSessionVault>();
        var current = await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        vault.Remove(current.Properties);
        vault.Remove(context.Properties);
    }

    public static async Task CaptureAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<ConsumerBillingOptions>();
        var vault = services.GetRequiredService<ConsumerBillingSessionVault>();
        vault.Remove(context.Properties);
        if (!options.IsConfigured || context.Options.ClientId != options.ClientId || context.Principal is null || context.Properties is null
            || !CanonicalIdentity(context.Principal, out var subject, out var session)
            || context.TokenEndpointResponse?.AccessToken is not { Length: > 0 and <= 16384 } token
            || context.Options.ConfigurationManager is null) return;
        try
        {
            var metadata = await context.Options.ConfigurationManager.GetConfigurationAsync(context.HttpContext.RequestAborted);
            if (!string.Equals(metadata.Issuer.TrimEnd('/'), options.Authority, StringComparison.Ordinal)) return;
            var clock = services.GetRequiredService<TimeProvider>();
            var expiry = ValidateToken(token, subject, session, options, metadata.SigningKeys, clock);
            if (expiry is null) return;
            var handle = vault.Capture(subject, session, token, expiry.Value);
            if (handle is not null) context.Properties.Items[ConsumerBillingSessionVault.TicketHandle] = handle;
        }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException or JsonException
            or InvalidOperationException or IOException or HttpRequestException) { /* Normal sign-in survives unavailable financial access. */ }
    }

    internal static DateTimeOffset? ValidateToken(string token, string subject, string session, ConsumerBillingOptions options,
        IEnumerable<SecurityKey> signingKeys, TimeProvider clock)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            using var json = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]), new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject()) if (!names.Add(property.Name)) return null;
            string? Text(string key) => json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            long? Seconds(string key) => json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var seconds) && seconds >= 0 && seconds.ToString(CultureInfo.InvariantCulture) == value.GetRawText() ? seconds : null;
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var expiry = Seconds("exp"); var authenticated = Seconds("auth_time");
            if (Text("sub") != subject || Text("sid") != session || Text("azp") != options.ClientId
                || Text("iss")?.TrimEnd('/') != options.Authority || Text("scope") is not { } scope
                || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(options.Scope, StringComparer.Ordinal)
                || expiry is null || authenticated is null || authenticated > now || authenticated >= expiry || now >= expiry
                || names.Contains("nbf") && (Seconds("nbf") is not { } start || start > now)
                || names.Contains("client_id") && Text("client_id") != options.ClientId
                || names.Contains("active") && json.RootElement.GetProperty("active").ToString() != "true"
                || new[] { "grant_type", "gty" }.Any(name => names.Contains(name)
                    && (Text(name) is null || Text(name) == "client_credentials"))) return null;
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = options.Authority, ValidateAudience = true, ValidAudience = options.Audience,
                ValidateIssuerSigningKey = true, IssuerSigningKeys = signingKeys, RequireSignedTokens = true,
                RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256],
                LifetimeValidator = (notBefore, expires, _, _) => expires.HasValue && expires.Value > clock.GetUtcNow().UtcDateTime
                    && (!notBefore.HasValue || notBefore.Value <= clock.GetUtcNow().UtcDateTime)
            }, out _);
            return DateTimeOffset.FromUnixTimeSeconds(expiry.Value);
        }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException or JsonException or FormatException) { return null; }
    }

    internal static bool CanonicalIdentity(ClaimsPrincipal principal, out string subject, out string session)
    {
        subject = ""; session = "";
        var subjects = principal.FindAll("sub").ToArray(); var sessions = principal.FindAll("sid").ToArray();
        if (principal.Identity?.IsAuthenticated != true || subjects.Length != 1 || sessions.Length != 1
            || !CanonicalGuid(subjects[0].Value) || !CanonicalGuid(sessions[0].Value)) return false;
        subject = subjects[0].Value; session = sessions[0].Value; return true;
    }
    private static bool CanonicalGuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
}
