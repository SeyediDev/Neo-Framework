using Neo.Domain.Entities.Common;

namespace Neo.Domain.Entities.Base;

public interface IBaseAuditableEntity: ISoftDelete
{
    DateTime CreateDate { get; set; }
	UserId? CreatedById { get; set; }
    DateTime LastModified { get; set; }
	UserId? LastModifiedById { get; set; }
}
