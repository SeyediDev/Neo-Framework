using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Concurrent;

namespace Fanasa.AccessManagement.Web.Governance;

/// <summary>A deployment-owned trust boundary. Tenant administrators cannot create or change these bindings.</summary>
public sealed class PipTransportOptions
{
    public PipEndpointBinding[] Bindings { get; set; } = [];

    public void Validate()
    {
        if (Bindings is null || Bindings.Length > 2000) throw new InvalidOperationException("Invalid PIP deployment bindings.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in Bindings)
        {
            if (binding is null || binding.TenantId == Guid.Empty) throw new InvalidOperationException("A PIP deployment binding needs a tenant.");
            var endpoint = CanonicalEndpoint(binding.Endpoint);
            if (!seen.Add(binding.TenantId.ToString("D") + "|" + endpoint)) throw new InvalidOperationException("Duplicate PIP deployment binding.");
            if (binding.SecretReference is { } reference && (string.IsNullOrWhiteSpace(reference) || reference.Length > 200 || reference.Any(char.IsControl)))
                throw new InvalidOperationException("Invalid PIP deployment secret reference.");
            if (binding.CaFile is not null && (!Path.IsPathFullyQualified(binding.CaFile) || !File.Exists(binding.CaFile)))
                throw new InvalidOperationException("PIP custom CA must be an existing absolute deployment-owned file.");
            if (binding.AllowedPrivateAddresses is null || binding.AllowedPrivateAddresses.Length > 32
                || binding.AllowedPrivateAddresses.Any(x => !IPAddress.TryParse(x, out var address) || !PipAddressPolicy.IsPrivate(address)))
                throw new InvalidOperationException("PIP private address exceptions must be exact RFC1918 or unique-local IP addresses.");
        }
    }

    public PipEndpointBinding Authorize(PipSourceDefinition source) => Authorize(source.TenantId, source.Endpoint, source.SecretReference);

    public PipEndpointBinding Authorize(Guid tenantId, string endpoint, string? secretReference)
    {
        var canonical = CanonicalEndpoint(endpoint);
        var binding = Bindings.SingleOrDefault(x => x.TenantId == tenantId
            && string.Equals(CanonicalEndpoint(x.Endpoint), canonical, StringComparison.Ordinal)
            && string.Equals(x.SecretReference, secretReference, StringComparison.Ordinal));
        return binding ?? throw new InvalidOperationException("PIP endpoint and secret are not approved for this tenant by the deployment configuration.");
    }

    public bool IsAuthorized(PipSourceDefinition source)
    {
        try { _ = Authorize(source); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
    }

    public static string CanonicalEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048 || endpoint.Any(char.IsControl)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("PIP endpoint must be an HTTPS URL without credentials, query parameters or fragments.");
        return uri.AbsoluteUri;
    }
}

public sealed class PipEndpointBinding
{
    public Guid TenantId { get; set; }
    public string Endpoint { get; set; } = "";
    public string? SecretReference { get; set; }
    public string[] AllowedPrivateAddresses { get; set; } = [];
    public string? CaFile { get; set; }
}

public interface IPipHttpClientProvider
{
    HttpClient GetClient(PipEndpointBinding binding);
}

/// <summary>One TLS policy per immutable deployment binding; custom roots never leak to another endpoint.</summary>
public sealed class PipHttpClientProvider : IPipHttpClientProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<HttpClient>> clients = new(StringComparer.Ordinal);
    public HttpClient GetClient(PipEndpointBinding binding)
    {
        var key = string.Join("|", binding.TenantId, PipTransportOptions.CanonicalEndpoint(binding.Endpoint), binding.SecretReference,
            binding.CaFile, string.Join(",", binding.AllowedPrivateAddresses.Order(StringComparer.Ordinal)));
        return clients.GetOrAdd(key, _ => new Lazy<HttpClient>(() => new HttpClient(PipHttpTransport.CreateHandler(binding.CaFile))
            { Timeout = Timeout.InfiniteTimeSpan })).Value;
    }
    public void Dispose() { foreach (var client in clients.Values) if (client.IsValueCreated) client.Value.Dispose(); }
}

/// <summary>IP policy is evaluated on resolved addresses, and the checked address is also the address connected to.</summary>
public static class PipAddressPolicy
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc && address.ScopeId == 0;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2 || bytes[1] == 88 && bytes[2] == 99)
                || bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)
                || bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        // Only global unicast is eligible; disallow transition/documentation ranges that can embed private IPv4 targets.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
            && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 1)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    public static bool IsApproved(IPAddress address, PipEndpointBinding binding) => IsPublic(address)
        || IsPrivate(address) && binding.AllowedPrivateAddresses.Any(x => IPAddress.TryParse(x, out var approved)
            && Normalize(approved).Equals(Normalize(address)));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

public static class PipHttpTransport
{
    internal static readonly HttpRequestOptionsKey<PipEndpointBinding> BindingKey = new("Pip.DeploymentBinding");

    public static SocketsHttpHandler CreateHandler(string? caFile = null)
    {
        var handler = new SocketsHttpHandler
        {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        // HTTP/1.1 and no pooled reuse ensure each request's binding governs its DNS check and connection.
        PooledConnectionLifetime = TimeSpan.Zero,
        MaxResponseHeadersLength = 16,
            ConnectCallback = ConnectAsync
        };
        // Also constrain the TLS stack's initial chain build before any custom callback executes.
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck };
        handler.SslOptions.CertificateChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        if (caFile is not null)
        {
            // Capture certificate bytes, not an ambient request or mutable global trust setting.
            using var configuredRoot = X509CertificateLoader.LoadCertificateFromFile(caFile);
            var rootBytes = configuredRoot.RawData;
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, peerChain, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var root = X509CertificateLoader.LoadCertificate(rootBytes);
                using var leaf = new X509Certificate2(certificate);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.DisableCertificateDownloads = true;
                if (peerChain is not null)
                    foreach (var element in peerChain.ChainElements.Cast<X509ChainElement>().Skip(1)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(leaf);
            };
        }
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(BindingKey, out var binding))
            throw new HttpRequestException("PIP connection has no deployment binding.");
        var approvedEndpoint = new Uri(PipTransportOptions.CanonicalEndpoint(binding.Endpoint));
        if (!string.Equals(context.DnsEndPoint.Host, approvedEndpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            || context.DnsEndPoint.Port != approvedEndpoint.Port)
            throw new HttpRequestException("PIP connection does not match the approved endpoint.");
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal) ? [literal]
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException) { throw new HttpRequestException("PIP DNS resolution failed."); }
        if (addresses.Length == 0 || addresses.Any(address => !PipAddressPolicy.IsApproved(address, binding)))
            throw new HttpRequestException("PIP DNS resolution includes an address outside the approved network boundary.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("PIP could not connect to an approved address.");
    }
}
