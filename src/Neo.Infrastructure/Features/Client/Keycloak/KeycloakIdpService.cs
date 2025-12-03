using Neo.Common.Extensions;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Client.Dto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Neo.Infrastructure.Features.Client.Keycloak;
public class KeycloakIdpService(
    HttpClient httpClient, IHttpClientFactory httpClientFactory, ILogger<KeycloakIdpService> logger, IConfiguration configuration)
    : IIdpService
{
    private string AdminBaseUri => configuration["IdpSetting:AdminBaseUri"]!;
    private string TokenUri => configuration["IdpSetting:TokenUri"]!;
    private string Authority => configuration["IdpSetting:Authority"]!;
    private string Issuer => configuration["IdpSetting:Issuer"]!;
    private string ClientId => configuration["IdpSetting:ClientId"]!;
    private string ClientSecret => configuration["IdpSetting:ClientSecret"]!;


    public async Task<bool> ValidateTokenAsync(string token)
    {
        var jwksUri = $"{Authority}/protocol/openid-connect/certs";
        var response = await httpClient.GetStringAsync(jwksUri);
        var keys = new JsonWebKeySet(response);

        var tokenHandler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,

            ValidateAudience = true,
            ValidAudience = ClientId,

            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.Keys
        };

        try
        {
            tokenHandler.ValidateToken(token, validationParameters, out _);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"----------- Reject 3 - {ex.Message}");
            return false;
        }
    }

    public async Task<IdpUserResponseDto> AddUserAsync(string userId, string mobile, string role)
    {
        await CreateUser(new IdpUserRequestDto()
        {
            username = mobile,
            attributes = new Attributes { userId = userId },
            credentials =
            [
                new Credential
                {
                    value = "FDXM5S7or4q35tWFr4R5ORPzZpc9Xj" // Default password
                }
            ]
        });
        var user = await GetUserAsync(mobile);
        await AddUserRoleAsync(user!.id, role);
        return user;
    }

    public async Task AddUserRoleAsync(string userId, string roleName)
    {
        var client = await GetClient();
        var role = await GetRoleByName(roleName);
        await AssignRoleAsync(userId, [new IdpRoleDto { Id = role!.Id, Name = role!.Name }]);
        //var user = await GetUserAsync(userId);
        //await AddUserRoleAsync(user!.id, roleName);
    }

    public async Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(CancellationToken cancellationToken)
    {
        return await GetClientCredentialTokenAsync(ClientId, ClientSecret, cancellationToken);
    }
	
    public async Task<IdpClientCredentialResponseDtp?> GetClientCredentialTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
	{
		try
		{
			var content = new FormUrlEncodedContent(
			[
				new KeyValuePair<string, string>("client_id", clientId!),
				new KeyValuePair<string, string>("client_secret", clientSecret),
				new KeyValuePair<string, string>("grant_type", "client_credentials")
			]);
			content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
			HttpResponseMessage response = await httpClient.PostAsync(TokenUri, content, cancellationToken);
			response.EnsureSuccessStatusCode();
			var result = await response.Content.ReadAsStringAsync(cancellationToken);
			return result.FromJson<IdpClientCredentialResponseDtp>();
		}
		catch (HttpRequestException ex)
		{
			logger.LogWarning("{@idp}", ex);
			return null;
		}
	}

	public async Task<TokenResponseDto?> GetUserTokenAsync(string mobile, CancellationToken cancellationToken)
    {
        try
        {
            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", ClientId!),
                new KeyValuePair<string, string>("client_secret", ClientSecret),
                new KeyValuePair<string, string>("username", mobile),
                new KeyValuePair<string, string>("password", "FDXM5S7or4q35tWFr4R5ORPzZpc9Xj"),
                new KeyValuePair<string, string>("grant_type", "password"),
                new KeyValuePair<string, string>("scope", "openid profile email roles"),
            ]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            HttpResponseMessage response = await httpClient.PostAsync(TokenUri, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            return result.FromJson<TokenResponseDto>();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{@idp}", ex);
            return null;
        }
    }

    public async Task<TokenResponseDto?> GetUserTokenByRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", ClientId!),
                new KeyValuePair<string, string>("client_secret", ClientSecret),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            ]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            HttpResponseMessage response = await httpClient.PostAsync(TokenUri, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            return result.FromJson<TokenResponseDto>();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{@idp}", ex);
            return null;
        }
    }

    public async Task<IdpUserResponseDto?> GetUserAsync(string mobile)
    {
        string url = $"{AdminBaseUri}/users?username={mobile}";
        HttpResponseMessage response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var users = await response.Content.ReadFromJsonAsync<List<IdpUserResponseDto>>();
        var user = users?.FirstOrDefault();
        if (user is null) return null;
        var roles = await GetUserRoles(user.id);
        user.Roles = roles?.Select(x => x.Name).ToArray();
        return user;
    }

    public async Task<HttpResponseMessage> GetClientCredentialsTokenAsync(string clientId, string clientSecret)
    {
        var formContent = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret)
        ]);

        var response = await httpClient.PostAsync(TokenUri, formContent);
        return response;
    }



    /// <summary>
    /// Get Token using username & password (Resource Owner Password Credentials Grant)
    /// </summary>
    public async Task<HttpResponseMessage> GetPasswordTokenAsync(string username, string password)
    {
        var formContent = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", ClientId!),
            new KeyValuePair<string, string>("client_secret", ClientSecret!),
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password)
        ]);

        return await httpClient.PostAsync(TokenUri, formContent);
    }

    /// <summary>
    /// Refresh Token
    /// </summary>
    public async Task<HttpResponseMessage> RefreshTokenAsync(string refreshToken)
    {
        var formContent = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", ClientId!),
            new KeyValuePair<string, string>("client_secret", ClientSecret!),
            new KeyValuePair<string, string>("refresh_token", refreshToken)
        ]);

        return await httpClient.PostAsync(TokenUri, formContent);
    }

    private async Task<List<IdpRoleDto>?> GetUserRoles(string userId)
    {
        var client = await GetClient();
        string url = $"{AdminBaseUri}/users/{userId}/role-mappings/clients/{client!.Id}";
        HttpResponseMessage response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsStringAsync();
        return result.FromJson<List<IdpRoleDto>>();
    }

    private async Task CreateUser(IdpUserRequestDto model)
    {
        string url = $"{AdminBaseUri}/users";
        string jsonPayload = model.ToJson();
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
    }

    private async Task<IdpClientResponse?> GetClient()
    {
        string url = $"{AdminBaseUri}/clients";
        HttpResponseMessage response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var clients = await response.Content.ReadFromJsonAsync<List<IdpClientResponse>>();
        return clients?.FirstOrDefault(x => x.ClientId == "home-care-user-management");
    }

    private async Task<IdpRoleDto?> GetRoleByName(string roleName)
    {
        string url = $"{AdminBaseUri}/roles/{roleName}";
        HttpResponseMessage response = await httpClient.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            return content.FromJson<IdpRoleDto>();
        }
        else if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return await AddRealmRole(roleName);
        }
        return null;
    }

    private async Task<IdpRoleDto?> AddRealmRole(string roleName)
    {
        string url = $"{AdminBaseUri}/roles";
        var body = new
        {
            name = roleName
        };
        var content = new StringContent(body.ToJson(), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await httpClient.PostAsync(url, content);
        if (response.IsSuccessStatusCode)
        {
            return await GetRoleByName(roleName);
        }
        return null;
    }

    private async Task AssignRoleAsync(string userId, List<IdpRoleDto> roles)
    {
        string url = $"{AdminBaseUri}/users/{userId}/role-mappings/realm";
        var content = new StringContent(roles.ToJson(), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
    }

    public async Task<UserInfoDto> userInfoRequest(string access_token)
    {
        var httpClient = httpClientFactory.CreateClient();
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{Authority}/protocol/openid-connect/userinfo");
        request.Headers.Add("Authorization", $"Bearer {access_token}");
        HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync()).FromJson<UserInfoDto>();
    }

    public async Task RemoveUserAsync(string mobile, string roleName)
    {
        var user = await GetUserAsync(mobile);
        var role = await GetRoleByName(roleName);
        if (role is not null)
            await RemoveUserRealmRole(user!.id, [role]);
        user = await GetUserAsync(mobile);
        if (!user!.Roles!.Any(x => x != "member" || x != "doctor" || x != "office"))
        {
            string url = $"{AdminBaseUri}/users/{user!.id}";
            HttpResponseMessage response = await httpClient.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task RemoveUserRealmRole(string userId, List<IdpRoleDto> roles)
    {
        string url = $"{AdminBaseUri}/users/{userId}/role-mappings/realm";
        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Delete,
            RequestUri = new Uri(url),
            Content = new StringContent(roles.ToJson(), Encoding.UTF8, "application/json")
        };
        HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
