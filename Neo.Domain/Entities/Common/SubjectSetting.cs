namespace Neo.Domain.Entities.Common;

public class SubjectSetting : BaseCoreCommonAuditableEntity<long>
{
    public string SubjectTitle { get; set; } = null!;//Folder
    public string SubjectId { get; set; } = null!;
    public string? Key { get; set; }
    public string? Value { get; set; }//Json
}