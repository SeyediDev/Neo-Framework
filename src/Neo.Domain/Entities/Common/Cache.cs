using System.ComponentModel.DataAnnotations;

namespace Neo.Domain.Entities.Common;

public class Cache : BaseCoreLogAuditableEntity<long>
{
    [MaxLength(128)]
    public string Key { get; set; } = null!;

	/// <summary>
	/// Json Serialized
	/// </summary>
	public string Value { get; set; } = null!;

}