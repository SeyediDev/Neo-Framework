namespace Neo.Domain.Features.Client.Dto;

public record IdpRoleDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
}
