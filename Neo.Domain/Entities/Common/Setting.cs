namespace Neo.Domain.Entities.Common;

public class Setting : BaseCoreCommonAuditableEntity<int>
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
