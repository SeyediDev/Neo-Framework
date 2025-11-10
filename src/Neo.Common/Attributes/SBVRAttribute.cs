using System.ComponentModel;

namespace Neo.Common.Attributes;

/// <summary>
/// In Display String attribute
/// 
/// This attribute determines that the field will be included in the basic display string of entity records
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true)]
public class SBVRAttribute(SBVRModality modality, string subject, string verbPhrase, string? condition = null) : Attribute
{
    public SBVRModality Modality { get; init; } = modality;
    public string? Subject { get; init; } = subject;
    public string VerbPhrase { get; init; } = verbPhrase;
    public string? Condition { get; init; } = condition;
}
public enum SBVRModality 
{
    /// <summary>
    /// الزامی
    /// </summary>
    [Description("الزامی")]
    Obligatory,
    /// <summary>
    /// ممنوع
    /// </summary>
    [Description("ممنوع")]
    Prohibited,
    /// <summary>
    /// ضروری
    /// </summary>
    [Description("ضروری")]
    Necessary,
    /// <summary>
    /// مجاز
    /// </summary>
    [Description("مجاز")]
    Permitted,
    /// <summary>
    /// توصیه شده
    /// </summary>
    [Description("توصیه شده")]
    Recommended,
    /// <summary>
    /// محاسبه شده
    /// </summary>
    [Description("محاسبه شده")]
    Calculated,
    /// <summary>
    /// پیش‌بینی شده
    /// </summary>
    [Description("پیش‌بینی شده")]
    Predicted,
    /// <summary>
    /// اختیاری
    /// </summary>
    [Description("اختیاری")]
    Optional
}
