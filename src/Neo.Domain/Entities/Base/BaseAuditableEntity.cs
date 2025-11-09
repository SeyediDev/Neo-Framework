using Neo.Common.Attributes;
using System.ComponentModel;

namespace Neo.Domain.Entities.Base;

public abstract class BaseAuditableEntity<TKey> : BaseEntity<TKey>, IBaseAuditableEntity
    where TKey : struct
{
    [DisplayName("تاریخ ایجاد")]
    public DateTime CreateDate { get; set; } = DateTime.Now;

    [DisplayName("تاریخ حذف")]
    public DateTime? ExpireDate { get; set; }
    [DefaultValue(false)]
    public bool IsDeleted { get; set; }
    //public DateTimeOffset Created { get; set; }

    [DisplayName("ایجاد کننده")]
    public int? CreatedById { get; set; }

    [DisplayName("تاریخ تغییر")]
    public DateTime LastModified { get; set; } = DateTime.Now;

    [DisplayName("تغییر دهنده")]
    public int? LastModifiedById { get; set; }
}

[Schema(nameof(DomainSchema.CoreConfig))]
[FileGroup(nameof(DomainSchema.CoreConfig))]
[ArchivePartition(nameof(DomainSchema.CoreConfig))]
public abstract class BaseCoreConfigAuditableEntity<TKey> : BaseAuditableEntity<TKey>
    where TKey : struct
{
}

[Schema(nameof(DomainSchema.Core))]
[FileGroup(nameof(DomainSchema.Core))]
[ArchivePartition(nameof(DomainSchema.Core))]
public abstract class BaseCoreAuditableEntity<TKey> : BaseAuditableEntity<TKey>
    where TKey : struct
{
}

[Schema(nameof(DomainSchema.CoreLog))]
[FileGroup(nameof(DomainSchema.CoreLog))]
[ArchivePartition(nameof(DomainSchema.CoreLog))]
public abstract class BaseCoreLogAuditableEntity<TKey> : BaseAuditableEntity<TKey>
    where TKey : struct
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ArchivePartitionAttribute(string name)
    : PartitionAttribute(nameof(ISoftDelete.IsDeleted), $"Archive_{name}", name, $"{name}_Archive")
{
}