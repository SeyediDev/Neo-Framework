namespace Neo.Common.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public class SchemaAttribute(string name) : Attribute
{
    public string Name = name;
}
