namespace Neo.Common.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class DataProviderAttribute(string name) : Attribute
{
    public string Name { get; set; } = name;
}
