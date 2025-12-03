using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Client.Dto;

namespace Neo.Infrastructure.Features.Client.Keycloak;

/// <summary>
/// پیاده‌سازی Token Service با استفاده از Memory (برای Development)
/// </summary>
public sealed class MemoryIdpService(ILogger<MemoryIdpService> logger, IConfiguration configuration) 
    : IIdpService
{
	private string ClientId => configuration["IdpSetting:ClientId"]!;
	private string ClientSecret => configuration["IdpSetting:ClientSecret"]!;
	private readonly Dictionary<string, TokenInfo> _tokenStore = new();
    private readonly Dictionary<string, string> _clientSecrets = new()
    {
        // پیش‌فرض برای باجت
        { "bajet-client", "bajet-secret" }
    };

    public Task<IdpUserResponseDto> AddUserAsync(string userId, string mobile, string role)
    {
        throw new NotImplementedException();
    }

	public Task AddUserRoleAsync(string userId, string role)
    {
        throw new NotImplementedException();
    }

	public async Task<HttpResponseMessage> GetClientCredentialsTokenAsync(string clientId, string clientSecret)
    {
        // اعتبارسنجی Client Credentials
        if (!_clientSecrets.TryGetValue(clientId, out var storedSecret) || storedSecret != clientSecret)
        {
            throw new UnauthorizedAccessException("Invalid client credentials");
        }

        // تولید JWT Token
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes("MemoryTokenService-SecretKey-For-Development-Only-Change-In-Production");
        var expiresIn = 3600; // 1 hour

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, clientId),
            new("client_id", clientId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddSeconds(expiresIn),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature
            ),
            Issuer = "Club.Channel.Api",
            Audience = "Club.Channel.Api"
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);

        // ذخیره توکن
        _tokenStore[tokenString] = new TokenInfo
        {
            ClientId = clientId,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn)
        };

        logger.LogInformation("Token generated for client {ClientId}", clientId);

		HttpResponseMessage result = new(System.Net.HttpStatusCode.OK)
		{
			Content = new StringContent(tokenString)
		};
		return await Task.FromResult( result);
    }


	public Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(CancellationToken cancellationToken)
    {
        return GetClientCredentialTokenAsync(ClientId, ClientSecret, cancellationToken);
    }

	public async Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
	{
		// اعتبارسنجی Client Credentials
		if (!_clientSecrets.TryGetValue(clientId, out var storedSecret) || storedSecret != clientSecret)
		{
			throw new UnauthorizedAccessException("Invalid client credentials");
		}

		// تولید JWT Token
		var tokenHandler = new JwtSecurityTokenHandler();
		var key = Encoding.UTF8.GetBytes("MemoryTokenService-SecretKey-For-Development-Only-Change-In-Production");
		var expiresIn = 3600; // 1 hour

		var claims = new List<Claim>
		{
			new(ClaimTypes.NameIdentifier, clientId),
			new("client_id", clientId),
			new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
			new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
		};

		var tokenDescriptor = new SecurityTokenDescriptor
		{
			Subject = new ClaimsIdentity(claims),
			Expires = DateTime.UtcNow.AddSeconds(expiresIn),
			SigningCredentials = new SigningCredentials(
				new SymmetricSecurityKey(key),
				SecurityAlgorithms.HmacSha256Signature
			),
			Issuer = "Club.Channel.Api",
			Audience = "Club.Channel.Api"
		};

		var token = tokenHandler.CreateToken(tokenDescriptor);
		var tokenString = tokenHandler.WriteToken(token);

		// ذخیره توکن
		_tokenStore[tokenString] = new TokenInfo
		{
			ClientId = clientId,
			ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn)
		};

		logger.LogInformation("Token generated for client {ClientId}", clientId);

		IdpClientCredentialResponseDtp? result = new()
		{
			access_token = tokenString,
			expires_in = expiresIn,
		};
		return await Task.FromResult(result);
	}

	public Task<HttpResponseMessage> GetPasswordTokenAsync(string username, string password)
    {
        throw new NotImplementedException();
    }

	public Task<IdpUserResponseDto?> GetUserAsync(string mobile)
    {
        throw new NotImplementedException();
    }

	public Task<TokenResponseDto?> GetUserTokenAsync(string mobile, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

	public Task<TokenResponseDto?> GetUserTokenByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

	public Task<HttpResponseMessage> RefreshTokenAsync(string refreshToken)
    {
        throw new NotImplementedException();
    }

	public Task RemoveUserAsync(string mobile, string role)
    {
        throw new NotImplementedException();
    }

	public Task<UserInfoDto> userInfoRequest(string access_token)
    {
        throw new NotImplementedException();
    }

	public async Task<bool> ValidateTokenAsync(string token)
	{
		try
		{
			// بررسی در store
			if (_tokenStore.TryGetValue(token, out var tokenInfo))
			{
				if (tokenInfo.ExpiresAt > DateTime.UtcNow)
				{
					return true;
				}
				// حذف توکن منقضی شده
				_tokenStore.Remove(token);
			}

			// اعتبارسنجی JWT
			var tokenHandler = new JwtSecurityTokenHandler();
			var key = Encoding.UTF8.GetBytes("MemoryTokenService-SecretKey-For-Development-Only-Change-In-Production");

			var validationParameters = new TokenValidationParameters
			{
				ValidateIssuerSigningKey = true,
				IssuerSigningKey = new SymmetricSecurityKey(key),
				ValidateIssuer = true,
				ValidIssuer = "Club.Channel.Api",
				ValidateAudience = true,
				ValidAudience = "Club.Channel.Api",
				ValidateLifetime = true,
				ClockSkew = TimeSpan.Zero
			};

			tokenHandler.ValidateToken(token, validationParameters, out _);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private class TokenInfo
    {
        public string ClientId { get; set; } = null!;
        public DateTime ExpiresAt { get; set; }
    }
}

