namespace Neo.FanasaIdentity.Infrastructure;

public sealed class FanasaIdentityOptions
{
    public const string SectionName = "FanasaIdentity";
    public string Authority { get; set; } = "http://127.0.0.1:18080/realms/neo";
    public string Audience { get; set; } = "neo-api";
    public bool RequireHttpsMetadata { get; set; }
}
