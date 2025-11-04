namespace Neo.Domain.Features.Client.Dto;

public record IdpClientResponse
{
    public required string Id { get; set; }
    public required string ClientId { get; set; }
}
