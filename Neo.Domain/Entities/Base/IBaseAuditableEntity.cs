namespace Neo.Domain.Entities.Base;

public interface IBaseAuditableEntity: ISoftDelete
{
    DateTime CreateDate { get; set; }
    int? CreatedById { get; set; }
    DateTime LastModified { get; set; }
    int? LastModifiedById { get; set; }
}
