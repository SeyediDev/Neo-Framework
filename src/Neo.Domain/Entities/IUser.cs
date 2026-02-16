namespace Neo.Domain.Entities;

public interface IUser<TKey> : IEntity<TKey>
    //where TKey : IStronglyTypedId<int>
{
    public long Mobile { get; set; }
    public int CountryCode { get; set; }
    public byte[]? OTPSeed { get; set; }
}
