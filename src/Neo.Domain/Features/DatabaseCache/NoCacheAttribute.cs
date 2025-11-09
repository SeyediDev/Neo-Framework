namespace Neo.Domain.Features.DatabaseCache;

/// <summary>
/// Helper attribute برای مشخص کردن entity هایی که نباید cache شوند
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class NoCacheAttribute : Attribute
{
}
