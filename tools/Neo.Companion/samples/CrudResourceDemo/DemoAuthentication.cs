using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CrudResourceDemo;

// Local teaching authentication only. Use the application's identity provider in production.
public sealed class DemoAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = configuration["DemoToken"];
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthenticateResult.NoResult());
        if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            return Task.FromResult(AuthenticateResult.Fail("Invalid demo token."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "demo-user"), new Claim("permission", "catalog.write")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
