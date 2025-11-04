namespace Neo.Common.Attributes;

/// <summary>
/// In Display String attribute
/// 
/// This attribute determines that the field will be included in the basic display string of entity records
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class InDisplayStringAttribute : Attribute
{
    /// <summary>
    /// اضافه کردن فیلد در رشته نمایش پایه موجودیت
    /// </summary>
    public string? OtherFieldThatIgnoreMe { get; set; }
    public string? Culture { get; set; }
    public bool WithTitle { get; set; }
    public bool IgnoreIfNull { get; set; }
}
