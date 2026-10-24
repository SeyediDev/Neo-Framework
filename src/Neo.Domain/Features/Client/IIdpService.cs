using Neo.Domain.Features.Client.Dto;

namespace Neo.Domain.Features.Client;
public interface IIdpService
{
    Task<bool> ValidateTokenAsync(string token);
    Task<IdpUserResponseDto> AddUserAsync(string userId, string mobile, string role);
    Task AddUserRoleAsync(string userId, string role);

    Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(CancellationToken cancellationToken);
    Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken);
    Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(string clientId, string clientSecret, string? channelKey, CancellationToken cancellationToken);
    /// <summary>
    /// Legacy mobile-only token request. Keycloak returns null because a mobile number
    /// is not proof of identity; use the configured identity provider's verified flow.
    /// </summary>
    Task<TokenResponseDto?> GetUserTokenAsync(string mobile, CancellationToken cancellationToken);
    Task<TokenResponseDto?> GetUserTokenByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
    Task<IdpUserResponseDto?> GetUserAsync(string mobile);

    Task<HttpResponseMessage> GetClientCredentialsTokenAsync(string clientId, string clientSecret);
    Task<HttpResponseMessage> GetPasswordTokenAsync(string username, string password);
    Task<HttpResponseMessage> RefreshTokenAsync(string refreshToken);
    Task<UserInfoDto> userInfoRequest(string access_token);
    Task RemoveUserAsync(string mobile, string role);
}
