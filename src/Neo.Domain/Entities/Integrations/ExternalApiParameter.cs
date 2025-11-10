using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Neo.Common.Attributes;

namespace Neo.Domain.Entities.Integrations;

public class ExternalApiParameter : BaseCoreConfigAuditableEntity<int>
{
    [DisplayName("شناسه API بیرونی")]
    public int ExternalApiId { get; set; }

    [DisplayName("API بیرونی")]
    public virtual ExternalApi ExternalApi { get; set; } = null!;

    [DisplayName("نام")]
    [InDisplayString]
    [MaxLength(128)]
    public string Name { get; set; } = null!;

    [DisplayName("شرح")]
    [MaxLength(512)]
    public string? Description { get; set; }

    [DisplayName("محل پارامتر")]
    public ExternalApiParameterLocation Location { get; set; } = ExternalApiParameterLocation.Query;

    [DisplayName("نوع داده")]
    public ExternalApiDataType DataType { get; set; } = ExternalApiDataType.String;

    [DisplayName("فرمت")]
    [MaxLength(64)]
    public string? Format { get; set; }

    [DisplayName("الزامی است")]
    public bool IsRequired { get; set; }

    [DisplayName("اجازه مقدار خالی")]
    public bool AllowEmptyValue { get; set; }

    [DisplayName("چند مقداری")]
    public bool Explode { get; set; }

    [DisplayName("ترتیب")]
    public int? Order { get; set; }

    [DisplayName("مقدار پیش‌فرض")]
    [MaxLength(512)]
    public string? DefaultValue { get; set; }

    [DisplayName("نمونه مقدار")]
    [MaxLength(1024)]
    public string? Example { get; set; }

    [DisplayName("مقادیر مجاز")]
    [Column(TypeName = "nvarchar(max)")]
    public string? EnumValuesJson { get; set; }

    [DisplayName("تعریف طرح")]
    [Column(TypeName = "nvarchar(max)")]
    public string? SchemaDefinitionJson { get; set; }

    [DisplayName("عبارت مپینگ")]
    [MaxLength(512)]
    public string? BindingExpression { get; set; }

    [DisplayName("کلید منبع داده")]
    [MaxLength(128)]
    public string? SourceKey { get; set; }

    [DisplayName("پارامتر والد")]
    public int? ParentParameterId { get; set; }

    public virtual ExternalApiParameter? ParentParameter { get; set; }

    [DisplayName("پارامترهای فرزند")]
    public virtual ICollection<ExternalApiParameter> Children { get; set; } = [];
}
