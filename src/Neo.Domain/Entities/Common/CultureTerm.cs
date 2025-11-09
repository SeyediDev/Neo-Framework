using Neo.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace Neo.Domain.Entities.Common;

public partial class CultureTerm : BaseCoreConfigAuditableEntity<int>
{
    [MaxLength(50)]
    public string SubjectTitle { get; set; } = null!;
    public int SubjectId { get; set; }
    [MaxLength(50)]
    public string SubjectField { get; set; } = null!;
    public int LanguageId { get; set; }
    public virtual Language Language { get; set; } = null!;
    [InDisplayString]
    [MaxLength(50)]
    public string Term { get; set; } = null!;
}
