using System.Text;
using System.Text.Json;
using Fanasa.HrReadCandidate;

internal static class SettingsTests
{
    public static void Run()
    {
        // Deliberately synthetic structural example, not a central record or operational grant.
        var config = new HrReadReceiverSettings
        {
            Enabled = false, SchemaVersion = HrReadDesign.SchemaVersion, ProfileId = HrReadDesign.ProfileId,
            RegisteredProductId = "11111111-1111-4111-8111-111111111111", ProductKey = HrReadDesign.ProductKey,
            Owner = "platform", OwnerTenantId = null, ConsumerTenantId = "33333333-3333-4333-8333-333333333333",
            SubjectId = "44444444-4444-4444-8444-444444444444", Issuer = "https://sso.example.test/realms/fanasa",
            Audience = HrReadDesign.Audience, ClientId = "test-hr-client", Environment = "development", Zone = "test-zone",
            ContractId = "test-contract", ContractVersion = 1, PolicyId = "test-policy", PolicyVersion = 1,
            AllocationId = "test-allocation", ConfigRevision = 1,
            CanonicalScope = "tenant=33333333-3333-4333-8333-333333333333;products=organization-fabric;environments=development;groups=organization.hr.read",
            KeyId = "test-key", Tier = "edge", PeerCertificateSha256 = new string('a', 64),
            MaximumResponseBytes = HrReadDesign.MaximumResponseBytes, MaximumWitnessSeconds = 5
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check.True(HrReadReceiverSettings.TryParse(json, out var parsed, out var errors) && parsed == config && errors.Count == 0, "valid structural config round trip");
        Check.True(!config.Enabled, "valid structural config does not enable an endpoint");
        foreach (var bad in new[] { config with { SchemaVersion = "9.0.0" }, config with { ProfileId = "fanasa.contract-policy/2" },
            config with { OwnerTenantId = "" }, config with { Owner = "" }, config with { OwnerTenantId = "22222222-2222-4222-8222-222222222222" }, config with { RegisteredProductId = "organization-fabric" }, config with { Audience = "fanasa-organization-fabric-api" },
            config with { Issuer = "http://sso.example.test/realms/fanasa" }, config with { Issuer = "https://user:secret@sso.example.test/realms/fanasa" },
            config with { Issuer = "https://sso.example.test/realms/fanasa?x=1" }, config with { ContractVersion = 0 }, config with { ConfigRevision = 0 },
            config with { Tier = "public" }, config with { PeerCertificateSha256 = "invalid" }, config with { MaximumResponseBytes = 1048577 },
            config with { MaximumResponseBytes = 0 }, config with { MaximumWitnessSeconds = 6 }, config with { CanonicalScope = "guessed" } })
            Check.True(bad.Validate().Count > 0, "invalid settings rejected");
        foreach (var malformed in new[] { "{}", "[]", "null", "{\"enabled\":false,\"enabled\":true}", "{\"secret\":\"never-accepted\"}" })
            Check.True(!HrReadReceiverSettings.TryParse(Encoding.UTF8.GetBytes(malformed), out _, out _), "malformed/duplicate/secret/partial configuration rejected");
        Check.True(!HrReadReceiverSettings.TryParse(new byte[65537], out _, out _), "configuration size bounded");
        Check.True(HrReadDesign.Action(HrReadEndpointKind.Chart) != HrReadDesign.Action(HrReadEndpointKind.History), "endpoint actions distinct");
        Check.Throws<ArgumentOutOfRangeException>(() => HrReadDesign.Action((HrReadEndpointKind)99), "unknown endpoint no fallback");
        var registered = new HrReadRegistration(Guid.Parse(config.RegisteredProductId), config.ProductKey, config.Audience, "platform", null);
        Check.True(config.MatchesCurrentRegistration(registered), "platform ownership matches actual nullable central shape");
        Check.True(!config.MatchesCurrentRegistration(registered with { Owner = "someone-else" }), "owner mismatch rejected");
        Check.True(!config.MatchesCurrentRegistration(registered with { TenantId = Guid.NewGuid() }), "owner tenant may not be invented");
        Check.True(!config.MatchesCurrentRegistration(registered with { Id = Guid.NewGuid() }), "same product key does not replace registered identity");
    }
}
