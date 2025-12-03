using Neo.Domain.Features.Client.Dto;

namespace Neo.Domain.Features.Client;
public interface IIdpService
{
    Task<bool> ValidateTokenAsync(string token);
    Task<IdpUserResponseDto> AddUserAsync(string userId, string mobile, string role);
    Task AddUserRoleAsync(string userId, string role);

    Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(CancellationToken cancellationToken);
    Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken);
    Task<TokenResponseDto?> GetUserTokenAsync(string mobile, CancellationToken cancellationToken);
    Task<TokenResponseDto?> GetUserTokenByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
    Task<IdpUserResponseDto?> GetUserAsync(string mobile);

    Task<HttpResponseMessage> GetClientCredentialsTokenAsync(string clientId, string clientSecret);
    Task<HttpResponseMessage> GetPasswordTokenAsync(string username, string password);
    Task<HttpResponseMessage> RefreshTokenAsync(string refreshToken);
    Task<UserInfoDto> userInfoRequest(string access_token);
    Task RemoveUserAsync(string mobile, string role);
}
