namespace Neo.Domain.Features.ObjectStore.Dto;

public class ObjectStoreDto
{
    public string? Type { get; set; }
    public List<Dictionary<string, object>>? Attributes { get; set; }
    public required byte[] Content { get; set; }
}
