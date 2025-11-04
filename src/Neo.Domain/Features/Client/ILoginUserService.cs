using Neo.Domain.Entities;

namespace Neo.Domain.Features.Client;

public interface ILoginUserService<TKey>
    where TKey : struct
{
    public Task<IUser<TKey>?> FindUser(string mobile, int countryCode, CancellationToken cancellationToken);
    public Task<IUser<TKey>> RegisterUser(string mobile, int countryCode, byte[] otpKey, 
        Func<IUser<int>, Task>? SetUseParametersInRegistration, CancellationToken cancellationToken);
    public Task VerifyUser(string mobile, int countryCode, CancellationToken cancellationToken);
}
