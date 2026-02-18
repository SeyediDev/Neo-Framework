using Neo.Common.Attributes;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Neo.Domain.Entities.Common;

[JsonConverter(typeof(StronglyTypedIdJsonConverterFactory))]
public readonly record struct LanguageId(int Value) : IStronglyTypedId<int>
{
	public override string ToString() => Value.ToString();

	public static explicit operator int(LanguageId id) => id.Value;

	public static explicit operator LanguageId(int id) => new(id);
}

public class Language : BaseCoreConfigAuditableEntity<LanguageId>
{
    [InDisplayString]
    [MaxLength(5)]
    public string Name { get; set; } = null!;
    [MaxLength(100)]
    public string? Description { get; set; }

    public virtual ICollection<CultureTerm> CultureTerm { get; set; } = [];
}
