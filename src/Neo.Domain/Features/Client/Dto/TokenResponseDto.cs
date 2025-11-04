namespace Neo.Domain.Features.Client.Dto;
public record TokenResponseDto
{
    public required string access_token { get; set; }
    public int expires_in { get; set; }
    public int refresh_expires_in { get; set; }
    public required string refresh_token { get; set; }
    public string token_type { get; set; } = "Bearer";
}
