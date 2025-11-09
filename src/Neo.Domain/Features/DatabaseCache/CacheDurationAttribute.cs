namespace Neo.Domain.Features.DatabaseCache;

/// <summary>
/// Helper attribute برای مشخص کردن مدت زمان cache
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class CacheDurationAttribute(int minutes) : Attribute
{
    public int Minutes { get; set; } = minutes;
}