using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Neo.Domain.Entities.Integrations;

public class ExternalApiResponse : BaseCoreConfigAuditableEntity<int>
{
    [DisplayName("شناسه API بیرونی")]
    public int ExternalApiId { get; set; }

    [DisplayName("API بیرونی")]
    public virtual ExternalApi ExternalApi { get; set; } = null!;

    [DisplayName("کد وضعیت")]
    [MaxLength(16)]
    public string StatusCode { get; set; } = "200";

    [DisplayName("شرح")]
    [MaxLength(512)]
    public string? Description { get; set; }

    [DisplayName("نوع رسانه")]
    [MaxLength(128)]
    public string MediaType { get; set; } = "application/json";

    [DisplayName("نوع دیتا")]
    public ExternalApiDataType DataType { get; set; } = ExternalApiDataType.Object;

    [DisplayName("فرمت")]
    [MaxLength(64)]
    public string? Format { get; set; }

    [DisplayName("نمونه پاسخ")]
    [Column(TypeName = "nvarchar(max)")]
    public string? ExampleJson { get; set; }

    [DisplayName("طرح پاسخ")]
    [Column(TypeName = "nvarchar(max)")]
    public string? SchemaDefinitionJson { get; set; }

    [DisplayName("هدرهای پاسخ")]
    [Column(TypeName = "nvarchar(max)")]
    public string? HeadersJson { get; set; }
}
