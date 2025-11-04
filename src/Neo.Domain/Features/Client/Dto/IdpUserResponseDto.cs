namespace Neo.Domain.Features.Client.Dto;
public record IdpUserResponseDto
{
    public required string id { get; set; }
    public required string username { get; set; }
    //public Dictionary<string, string[]> Attributes { get; set; } = null!;
    public string[]? Roles { get; set; }
}
