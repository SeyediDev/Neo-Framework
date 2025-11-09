using Neo.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace Neo.Domain.Entities.Common;

public class Language : BaseCoreConfigAuditableEntity<int>
{
    [InDisplayString]
    [MaxLength(5)]
    public string Name { get; set; } = null!;
    [MaxLength(100)]
    public string? Description { get; set; }

    public virtual ICollection<CultureTerm> CultureTerm { get; set; } = [];
}
