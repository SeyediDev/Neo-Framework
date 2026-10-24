using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Fanasa.AccessManagement.Web.Security;

/// <summary>A dedicated TLS upstream requests the Gateway certificate without changing human-facing listeners.</summary>
public static class OrganizationHrHosting
{
    public const string EndpointName = "OrganizationHr";

    public static WebApplicationBuilder ConfigureOrganizationHrUpstream(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Organization:HrApi:Enabled")) return builder;
        var settings = builder.Configuration.GetSection("Organization:HrApi").Get<OrganizationHrOptions>() ?? new();
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Organization:HrApi requires complete service identity and Gateway certificate settings.");
        var endpointUrl = builder.Configuration[$"Kestrel:Endpoints:{EndpointName}:Url"];
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/")
            throw new InvalidOperationException("An explicit HTTPS Kestrel endpoint named OrganizationHr is required.");

        var pin = Convert.FromHexString(settings.GatewayCertificateSha256!);
        builder.WebHost.ConfigureKestrel((context, options) =>
            options.Configure(context.Configuration.GetSection("Kestrel"), reloadOnChange: false)
                .Endpoint(EndpointName, endpointOptions =>
                {
                    if (!endpointOptions.IsHttps) throw new InvalidOperationException("OrganizationHr must use TLS.");
                    // AllowCertificate permits a clean HTTP 403 for a missing peer certificate.
                    // The HR boundary additionally requires a matching actual TLS peer on every request.
                    endpointOptions.HttpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    endpointOptions.HttpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                        IsPinnedGatewayCertificate(certificate, pin);
                }));
        return builder;
    }

    public static bool IsPinnedGatewayCertificate(X509Certificate2 certificate, ReadOnlySpan<byte> pin)
    {
        var now = DateTime.UtcNow;
        if (pin.Length != 32 || now < certificate.NotBefore.ToUniversalTime()
            || now >= certificate.NotAfter.ToUniversalTime()
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), pin)) return false;
        // A provisioned certificate must explicitly support TLS client authentication.
        return certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));
    }
}
