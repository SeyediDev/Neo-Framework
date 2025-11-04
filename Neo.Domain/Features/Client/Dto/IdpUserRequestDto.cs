namespace Neo.Domain.Features.Client.Dto;
public record IdpUserRequestDto
{
    public required string username { get; set; }
    public bool enabled { get; set; } = true;
    public required Attributes attributes { get; set; }
    public required List<Credential> credentials { get; set; }
}

public record Attributes
{
    public required string userId { get; set; }
}

public record Credential
{
    public string type { get; set; } = "password";
    public required string value { get; set; }
    public bool temporary { get; set; } = false;
}
