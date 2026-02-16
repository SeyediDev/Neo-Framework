using System.ComponentModel;
using Neo.Common.Attributes;
using Neo.Domain.Entities.Common;

namespace Neo.Domain.Entities.Base;

public abstract class BaseStronglyKeyEntity<TKey, TValue> : BaseEntity<TKey>
	where TKey : IStronglyTypedId<TValue>
{

}
public abstract class BaseAuditableStronglyKeyEntity<TKey, TValue> : BaseStronglyKeyEntity<TKey, TValue>, IBaseAuditableEntity
	where TKey : IStronglyTypedId<TValue>
	where TValue : struct
{
    [DisplayName("تاریخ ایجاد")]
    public DateTime CreateDate { get; set; } = DateTime.UtcNow;

	[DisplayName("تاریخ حذف")]
    public DateTime? ExpireDate { get; set; }
    [DefaultValue(false)]
    public bool IsDeleted { get; set; }
    //public DateTimeOffset Created { get; set; }

    [DisplayName("ایجاد کننده")]
    public UserId? CreatedById { get; set; }

    [DisplayName("تاریخ تغییر")]
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    [DisplayName("تغییر دهنده")]
    public UserId? LastModifiedById { get; set; }
}

[Schema(nameof(DomainSchema.CoreConfig))]
[FileGroup(nameof(DomainSchema.CoreConfig))]
[ArchivePartition(nameof(DomainSchema.CoreConfig))]
public abstract class BaseCoreConfigAuditableStronglyKeyEntity<TKey, TValue> : BaseAuditableStronglyKeyEntity<TKey, TValue>
	where TKey : IStronglyTypedId<TValue>
	where TValue : struct
{
}

[Schema(nameof(DomainSchema.Core))]
[FileGroup(nameof(DomainSchema.Core))]
[ArchivePartition(nameof(DomainSchema.Core))]
public abstract class BaseCoreAuditableStronglyKeyEntity<TKey, TValue> : BaseAuditableStronglyKeyEntity<TKey, TValue>
	where TKey : IStronglyTypedId<TValue>
	where TValue : struct
{
}

[Schema(nameof(DomainSchema.CoreLog))]
[FileGroup(nameof(DomainSchema.CoreLog))]
[ArchivePartition(nameof(DomainSchema.CoreLog))]
public abstract class BaseCoreLogAuditableStronglyKeyEntity<TKey, TValue> : BaseAuditableStronglyKeyEntity<TKey, TValue>
	where TKey : IStronglyTypedId<TValue>
	where TValue : struct
{
}