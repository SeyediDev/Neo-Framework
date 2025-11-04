namespace Neo.Domain.Entities.Base;

public interface ISoftDelete
{
    DateTime? ExpireDate { get; set; }
    bool IsDeleted { get; set; }
}
