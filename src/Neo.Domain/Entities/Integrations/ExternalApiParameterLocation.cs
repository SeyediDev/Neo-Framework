using System.ComponentModel;

namespace Neo.Domain.Entities.Integrations;

public enum ExternalApiParameterLocation
{
    [Description("مسیر")]
    Path = 1,
    [Description("پرس‌وجو")]
    Query = 2,
    [Description("هدر")]
    Header = 3,
    [Description("کوکی")]
    Cookie = 4,
    [Description("بدنه")]
    Body = 5
}
