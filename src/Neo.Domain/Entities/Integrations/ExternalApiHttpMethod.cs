using System.ComponentModel;

namespace Neo.Domain.Entities.Integrations;

public enum ExternalApiHttpMethod
{
    [Description("GET")]
    Get = 1,
    [Description("POST")]
    Post = 2,
    [Description("PUT")]
    Put = 3,
    [Description("DELETE")]
    Delete = 4,
    [Description("PATCH")]
    Patch = 5,
    [Description("HEAD")]
    Head = 6,
    [Description("OPTIONS")]
    Options = 7
}
