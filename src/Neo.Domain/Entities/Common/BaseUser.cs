using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Neo.Common.Attributes;

namespace Neo.Domain.Entities.Common;

public enum UserId : int { }

//[JsonConverter(typeof(StronglyTypedIdJsonConverterFactory))]
//public readonly record struct UserId : IStronglyTypedId<int>
//{
//	public int Value { get; init; }

//	public UserId(int value)
//	{
//		Value = value;
//	}

//	public override string ToString() => Value.ToString();

//	public static explicit operator int(UserId id) => id.Value;

//	public static explicit operator UserId(int id) => new(id);
//}

public abstract partial class BaseUser : BaseCoreAuditableEntity<UserId>, IUser<UserId>
{
	[InDisplayString]
	public long Mobile { get; set; }
	public int CountryCode { get; set; }
	[MaxLength(50)]
	public byte[]? OTPSeed { get; set; }

	[MaxLength(250)]
	public string? Email { get; set; }

	public bool Verified { get; set; }
}