namespace Neo.Common.Attributes;

/// <summary>
/// توضیح
/// </summary>
/// <remarks>
/// توضیح
/// </remarks>
/// <param name="name">نام فارسی</param>
/// <param name="enName">نام انگلیسی</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Enum)]
public class EnumDescriptionAttribute(string name, string? enName = null) : Attribute
{
    public string Name { get; set; } = name;
    public string? EnName { get; set; } = enName;
}
