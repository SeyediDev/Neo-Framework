using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Neo.AgentOrchestration.Web;

// Only called under the ticket's gate. No API request (especially POST) is replayed.
public sealed class WebTokenRefresher(
    IHttpClientFactory clients, IOptionsMonitor<OpenIdConnectOptions> options,
    IConfiguration configuration, TimeProvider clock)
{
    public async Task<bool> RefreshIfNeededAsync(AuthenticationTicket ticket)
    {
        var now = clock.GetUtcNow();
        if (ticket.Properties.ExpiresUtc is not { } deadline || deadline <= now) return false;
        var rawExpiry = ticket.Properties.GetTokenValue("expires_at");
        if (!DateTimeOffset.TryParse(rawExpiry, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiry)) return false;
        if (expiry > now.AddSeconds(45)) return true;
        var refresh = ticket.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrWhiteSpace(refresh)) return false;
        try
        {
            var oidc = options.Get(OpenIdConnectDefaults.AuthenticationScheme);
            var metadata = await oidc.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);
            if (!Uri.TryCreate(oidc.Authority, UriKind.Absolute, out var authority) ||
                metadata.Issuer.TrimEnd('/') != authority.AbsoluteUri.TrimEnd('/') ||
                !Uri.TryCreate(metadata.TokenEndpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.GetLeftPart(UriPartial.Authority) != authority.GetLeftPart(UriPartial.Authority) ||
                (endpoint.Scheme != "https" && (oidc.RequireHttpsMetadata || !endpoint.IsLoopback)) ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)) return false;
            var audience = configuration["WebAuthentication:ApiAudience"] ?? configuration["Authentication:Audience"];
            if (string.IsNullOrWhiteSpace(audience)) return false;
            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = oidc.ClientId!, ["refresh_token"] = refresh
            };
            if (!string.IsNullOrEmpty(oidc.ClientSecret)) fields["client_secret"] = oidc.ClientSecret;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                { Content = new FormUrlEncodedContent(fields) };
            using var client = clients.CreateClient("WebSessionRefresh");
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("expires_in", out var seconds) || !seconds.TryGetInt32(out var lifetime) || lifetime <= 0)
                return false;
            var accessToken = access.GetString();
            var handler = new JsonWebTokenHandler { MapInboundClaims = false };
            async Task<TokenValidationResult> Validate(string token, string expectedAudience) =>
                await handler.ValidateTokenAsync(token, new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = metadata.Issuer,
                    ValidateAudience = true, ValidAudience = expectedAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKeys = metadata.SigningKeys,
                    RequireSignedTokens = true, RequireExpirationTime = true,
                    ValidateLifetime = true, ClockSkew = TimeSpan.Zero
                });
            var validation = await Validate(accessToken!, audience);
            var subject = ticket.Principal.FindAll("sub").Select(c => c.Value).ToArray();
            if (!validation.IsValid || subject.Length != 1 || string.IsNullOrWhiteSpace(subject[0]) ||
                !SameSubject(validation.ClaimsIdentity, subject[0])) return false;
            // The API remains authoritative for grants on every call. Validate a new
            // ID token if supplied, but don't replace the original login principal.
            string? idToken = null;
            if (root.TryGetProperty("id_token", out var id))
            {
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) return false;
                idToken = id.GetString();
                var idValidation = await Validate(idToken!, oidc.ClientId!);
                if (!idValidation.IsValid || !SameSubject(idValidation.ClaimsIdentity, subject[0])) return false;
            }
            var nextRefresh = refresh;
            if (root.TryGetProperty("refresh_token", out var rotated))
            {
                if (rotated.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rotated.GetString())) return false;
                nextRefresh = rotated.GetString();
            }
            // Cap both provider expiry and the unchanged absolute session deadline.
            var jwtExpiry = validation.SecurityToken.ValidTo;
            var nextExpiry = new[] { now.AddSeconds(lifetime), new DateTimeOffset(jwtExpiry, TimeSpan.Zero), deadline }.Min();
            if (nextExpiry <= clock.GetUtcNow().AddSeconds(5)) return false;
            var tokens = ticket.Properties.GetTokens().ToDictionary(t => t.Name, t => t.Value);
            tokens["access_token"] = accessToken!;
            tokens["refresh_token"] = nextRefresh!;
            tokens["expires_at"] = nextExpiry.ToString("o", CultureInfo.InvariantCulture);
            if (idToken is not null) tokens["id_token"] = idToken;
            ticket.Properties.StoreTokens(tokens.Select(t => new AuthenticationToken { Name = t.Key, Value = t.Value }));
            return true;
        }
        // Ambiguous refresh/network failures never retry a possibly consumed refresh
        // token. Reauthentication is safe; neither credentials nor error bodies log.
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or
            JsonException or SecurityTokenException or InvalidOperationException or ArgumentException or OverflowException)
        { return false; }
    }

    private static bool SameSubject(ClaimsIdentity identity, string expected)
    {
        var subjects = identity.FindAll("sub").Select(c => c.Value).ToArray();
        return subjects.Length == 1 && subjects[0] == expected;
    }
}
