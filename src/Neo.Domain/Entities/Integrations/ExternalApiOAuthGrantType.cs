using System.ComponentModel;

namespace Neo.Domain.Entities.Integrations;

public enum ExternalApiOAuthGrantType
{
    [Description("بدون OAuth")]
    None = 0,
    [Description("Client Credentials")]
    ClientCredentials = 1,
    [Description("Resource Owner Password")]
    Password = 2,
    [Description("Authorization Code (با client credential ذخیره شده)")]
    AuthorizationCode = 3,
    [Description("Device Code")]
    DeviceCode = 4,
    [Description("گرنت اختصاصی")]
    Custom = 9
}
