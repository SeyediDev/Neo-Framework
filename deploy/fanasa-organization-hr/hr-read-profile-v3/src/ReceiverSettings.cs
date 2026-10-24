using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fanasa.HrReadCandidate;

/// <summary>New designed read-only wire profile. Technical ceilings never create quota, price, grant or allocation.</summary>
public static class HrReadDesign
{
    public const string SchemaVersion = "3.0.0";
    public const string ProfileId = "fanasa.organization-hr-read/1";
    public const string ProductKey = "organization-fabric";
    public const string Audience = "fanasa-organization-fabric";
    public const string ReadScope = "organization.hr.read";
    public const string Group = "organization.hr.read";
    public const string Template = "organization-hr-read/1";
    public const string Role = "HrReader";
    public const string Permission = "organization.read";
    public const int MaximumResponseBytes = 1048576;
    public const int MaximumWitnessSeconds = 5;
    public static string Action(HrReadEndpointKind endpoint) => endpoint switch
    {
        HrReadEndpointKind.Chart => "organization.hr.chart.read",
        HrReadEndpointKind.History => "organization.hr.history.read",
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
    };
}

/// <summary>Nonsecret deployment intent; validation is structural, never runtime authority or permission.</summary>
public sealed record HrReadReceiverSettings
{
    public required bool Enabled { get; init; }
    public required string SchemaVersion { get; init; }
    public required string ProfileId { get; init; }
    public required string RegisteredProductId { get; init; }
    public required string ProductKey { get; init; }
    public required string Owner { get; init; }
    public required string? OwnerTenantId { get; init; }
    public required string ConsumerTenantId { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string ClientId { get; init; }
    public required string SubjectId { get; init; }
    public required string Environment { get; init; }
    public required string Zone { get; init; }
    public required string ContractId { get; init; }
    public required long ContractVersion { get; init; }
    public required string PolicyId { get; init; }
    public required long PolicyVersion { get; init; }
    public required string AllocationId { get; init; }
    public required long ConfigRevision { get; init; }
    public required string CanonicalScope { get; init; }
    public required string KeyId { get; init; }
    public required string Tier { get; init; }
    public required string PeerCertificateSha256 { get; init; }
    public required int MaximumResponseBytes { get; init; }
    public required int MaximumWitnessSeconds { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Require(bool valid, string field) { if (!valid) errors.Add(field); }
        Require(SchemaVersion == HrReadDesign.SchemaVersion, "schemaVersion");
        Require(ProfileId == HrReadDesign.ProfileId, "profileId");
        Require(ProductKey == HrReadDesign.ProductKey, "productKey");
        Require(Audience == HrReadDesign.Audience, "audience");
        Require(CanonicalGuid(RegisteredProductId), "registeredProductId");
        // The observed central product is platform-owned, with no owning tenant.
        // Preserve that exact distinction; never turn the string 'platform' into a tenant UUID.
        Require(Owner == "platform" ? OwnerTenantId is null : CanonicalGuid(Owner) && Owner == OwnerTenantId, "ownership");
        Require(CanonicalGuid(ConsumerTenantId), "consumerTenantId");
        Require(CanonicalGuid(SubjectId), "subjectId");
        Require(Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) && issuer.Scheme == "https"
            && string.IsNullOrEmpty(issuer.UserInfo) && string.IsNullOrEmpty(issuer.Query) && string.IsNullOrEmpty(issuer.Fragment)
            && issuer.AbsolutePath == "/realms/fanasa" && Issuer == issuer.AbsoluteUri, "issuer");
        foreach (var (name, value) in new[] { ("clientId", ClientId), ("environment", Environment), ("zone", Zone),
                     ("contractId", ContractId), ("policyId", PolicyId), ("allocationId", AllocationId), ("keyId", KeyId) })
            Require(Slug(value), name);
        Require(ContractVersion is > 0 and <= 9007199254740991L, "contractVersion");
        Require(PolicyVersion is > 0 and <= 9007199254740991L, "policyVersion");
        Require(ConfigRevision is > 0 and <= 9007199254740991L, "configRevision");
        Require(Tier is "edge" or "internal", "tier");
        Require(PeerCertificateSha256 is { Length: 64 } && PeerCertificateSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "peerCertificateSha256");
        Require(MaximumResponseBytes is > 0 and <= HrReadDesign.MaximumResponseBytes, "maximumResponseBytes");
        Require(MaximumWitnessSeconds is > 0 and <= HrReadDesign.MaximumWitnessSeconds, "maximumWitnessSeconds");
        var scope = $"tenant={ConsumerTenantId};products={ProductKey};environments={Environment};groups={HrReadDesign.Group}";
        Require(CanonicalScope == scope, "canonicalScope");
        return errors.AsReadOnly();
    }

    public static bool TryParse(ReadOnlyMemory<byte> json, out HrReadReceiverSettings? settings, out IReadOnlyList<string> errors)
    {
        settings = null;
        if (json.Length is 0 or > 65536) { errors = ["configuration-size"]; return false; }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!keys.Add(property.Name)) throw new JsonException();
            settings = document.RootElement.Deserialize<HrReadReceiverSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 });
            if (settings is null) throw new JsonException();
            errors = settings.Validate();
            if (errors.Count == 0) return true;
            settings = null; return false;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { errors = ["invalid-configuration-json"]; settings = null; return false; }
    }
    private static bool CanonicalGuid(string? value) => Guid.TryParseExact(value, "D", out var parsed)
        && parsed != Guid.Empty && parsed.ToString("D", CultureInfo.InvariantCulture) == value;
    private static bool Slug(string? value) => value is { Length: > 0 and <= 64 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    /// <summary>Compare to freshly read central metadata. Structural equality alone is NOT an access grant.</summary>
    public bool MatchesCurrentRegistration(HrReadRegistration registration) => registration is not null
        && Validate().Count == 0 && Guid.Parse(RegisteredProductId) == registration.Id
        && ProductKey == registration.Key && Audience == registration.Audience && Owner == registration.Owner
        && OwnerTenantId == registration.TenantId?.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Request snapshot of the existing central registry, not a new registry or persisted catalog.</summary>
public sealed record HrReadRegistration(Guid Id, string Key, string Audience, string Owner, Guid? TenantId);
