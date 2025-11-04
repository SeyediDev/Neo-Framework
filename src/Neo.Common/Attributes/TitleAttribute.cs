using System.Resources;

namespace Neo.Common.Attributes;

[AttributeUsage(AttributeTargets.All)]
public class TitleAttribute : Attribute
{
    public string? Title { get; }
    public TitleAttribute(string resourceName, Type resourceType)
    {
        Title = GetResourceLookup(resourceName, resourceType);
    }
    public TitleAttribute(string title)
    {
        Title = title;
    }

    private static string? GetResourceLookup(string resourceName, Type resourceType)
    {
        return string.IsNullOrEmpty(resourceName)
            ? throw new ArgumentNullException(nameof(resourceName))
            : resourceType == null
            ? throw new ArgumentNullException(nameof(resourceType))
            : new ResourceManager(resourceType).GetString(resourceName);
    }
}
