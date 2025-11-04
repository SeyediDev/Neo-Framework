namespace Neo.Common.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PartitionAttribute(string partitionFieldId, string partitionScheme, params string[] fileGroups) : Attribute
{
    public string PartitionFieldId { get; } = partitionFieldId;
    public string PartitionScheme { get; } = partitionScheme;
    public string[] FileGroups { get; } = fileGroups.Length>0
            ? fileGroups
            : [!string.IsNullOrEmpty(partitionScheme) ? "FG_" + partitionScheme : ""];
}
