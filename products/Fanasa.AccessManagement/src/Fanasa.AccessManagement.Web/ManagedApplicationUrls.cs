using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Fanasa.AccessManagement.Web.Platform;

/// <summary>Approved managed application names during the public-domain migration.</summary>
public static partial class ManagedApplicationUrls
{
    public const string LegacyDeveloper = "https://developer.fanasa.net.local";
    public const string PublicDeveloper = "https://developer.app.fanasacloud.ir";
    public static bool IsLegacyHost(string host) => host.EndsWith(".fanasa.net.local", StringComparison.OrdinalIgnoreCase)
        && Uri.CheckHostName(host) == UriHostNameType.Dns && !host.EndsWith('.');
    public static bool IsPublicHost(string host) => PublicHost().IsMatch(host);
    public static bool IsManagedHost(string host) => IsLegacyHost(host) || IsPublicHost(host);
    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)?app\\.fanasacloud\\.ir\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicHost();

    public static string DeveloperBase(IConfiguration configuration)
    {
        var value = configuration["Platform:DeveloperPublicUrl"] ?? LegacyDeveloper;
        if (value != LegacyDeveloper && value != PublicDeveloper)
            throw new InvalidOperationException("Platform:DeveloperPublicUrl must be an approved exact developer HTTPS origin.");
        return value;
    }
    public static bool IsDeveloperRuntime(string? url, Guid applicationId, IConfiguration configuration) =>
        url == DeveloperBase(configuration) + "/runtime/" + applicationId.ToString("D") + "/"
        || url == LegacyDeveloper + "/runtime/" + applicationId.ToString("D") + "/";
}

/// <summary>System trust for public names; the existing private root only for legacy names.</summary>
public static class PlatformEndpointTls
{
    public static HttpClientHandler CreateHandler(string caFile)
    {
        var ca = X509CertificateLoader.LoadCertificateFromFile(caFile);
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                Validate(request.RequestUri, certificate, errors, ca)
        };
    }
    public static bool Validate(Uri? endpoint, X509Certificate2? certificate, SslPolicyErrors errors, X509Certificate2 privateRoot)
    {
        if (endpoint is null || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || certificate is null
            || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        if (ManagedApplicationUrls.IsPublicHost(endpoint.IdnHost)) return errors == SslPolicyErrors.None;
        if (!ManagedApplicationUrls.IsLegacyHost(endpoint.IdnHost)) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(privateRoot);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }
}
