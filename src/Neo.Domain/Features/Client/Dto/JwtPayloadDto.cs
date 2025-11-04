namespace Neo.Domain.Features.Client.Dto;
public class RealmAccess
{
    public List<string>? roles { get; set; }
}

public class JwtPayloadDto
{
    public int exp { get; set; }
    public int iat { get; set; }
    public string? jti { get; set; }
    public string? iss { get; set; }
    public string? aud { get; set; }
    public string? sub { get; set; }
    public string? typ { get; set; }
    public string? azp { get; set; }
    public string? sid { get; set; }
    public string? acr { get; set; }

    //[JsonProperty("allowed-origins")]
    //public List<string> allowedorigins { get; set; }
    public RealmAccess? realm_access { get; set; }
    public string? scope { get; set; }
    public bool email_verified { get; set; }
    public int userid { get; set; }
    public string? username { get; set; }
}