namespace Neo.Domain.Features.Client.Dto;
public record IdpClientCredentialResponseDtp
{
    public string access_token { get; set; } = null!;
    public int expires_in { get; set; }
}
