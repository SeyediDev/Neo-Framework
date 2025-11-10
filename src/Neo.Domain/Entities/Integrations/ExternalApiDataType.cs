using System.ComponentModel;

namespace Neo.Domain.Entities.Integrations;

public enum ExternalApiDataType
{
    [Description("رشته")]
    String = 1,
    [Description("عدد صحیح")]
    Integer = 2,
    [Description("عدد اعشاری")]
    Number = 3,
    [Description("منطقی")]
    Boolean = 4,
    [Description("شیء")]
    Object = 5,
    [Description("آرایه")]
    Array = 6,
    [Description("شناسه ارجاعی")]
    Reference = 7
}
