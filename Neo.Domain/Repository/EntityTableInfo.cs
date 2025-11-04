namespace Neo.Domain.Repository;

public class EntityTableInfo
{
    public string? TableName { get; set; }
    public string? Schema { get; set; }
    public Dictionary<string,EntityFieldColumnInfo> Properties { get; set; } = [];
    public List<EntityFieldColumnInfo> PrimaryKeys { get; set; } = [];
}

public class EntityFieldColumnInfo
{
    public required string Id { get; set; }
    public string? Name { get; set; }
    public string? Comment { get; set; }
    public bool IsIdentity { get; set; }
}
