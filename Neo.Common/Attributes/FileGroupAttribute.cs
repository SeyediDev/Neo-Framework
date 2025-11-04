namespace Neo.Common.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public class FileGroupAttribute(string name) : Attribute
{
    public string Name = name;
}
