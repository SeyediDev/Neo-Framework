namespace Neo.Domain.Entities.Common;

public class Setting : BaseCoreConfigAuditableEntity<int>
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
