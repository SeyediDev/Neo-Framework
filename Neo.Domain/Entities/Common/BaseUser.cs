using Neo.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace Neo.Domain.Entities.Common;

public abstract partial class BaseUser : BaseCoreCommonAuditableEntity<int>, IUser<int>
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