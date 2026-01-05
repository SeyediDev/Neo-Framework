using Neo.Domain.Features.Captchas;
using Neo.Domain.Features.Client;
using Neo.Infrastructure.Features.Captchas;
using Neo.Infrastructure.Features.Client.Keycloak;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Neo.Infrastructure.Features.Client.Memory;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Neo.Infrastructure.Features.Client;

public static class DependencyInjection
{
    public static AuthenticationBuilder AddNeoAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var authenticationBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
        authenticationBuilder.AddJwtBearer(options =>
        {
            // Check if we're using Memory token provider
            var tokenProvider = configuration["IdpSetting:TokenProvider"] ?? "Memory";
            var isMemoryProvider = tokenProvider.Equals("Memory", StringComparison.OrdinalIgnoreCase);
            
            // Only set Authority if not using Memory provider (Memory provider uses fixed key)
            if (!isMemoryProvider)
            {
                options.Authority = configuration["IdpSetting:Authority"];
                options.Audience = configuration["IdpSetting:ClientId"];
            }
            else
            {
                // For Memory provider, don't set Authority (we use fixed signing key)
                // Setting Authority to null or empty prevents metadata endpoint calls
                options.Authority = null;
                options.Audience = configuration["IdpSetting:ClientId"] ?? "Club.Channel.Api";
                // Note: MetadataAddress and ConfigurationManager cannot be set to null
                // But by not setting Authority, they won't be used
            }
            
            options.RequireHttpsMetadata = false;
            
            // Set ValidIssuer and ValidAudience based on provider
            var validIssuer = isMemoryProvider 
                ? "Club.Channel.Api"  // Fixed issuer for Memory provider
                : (configuration["IdpSetting:Authority"] ?? "Club.Channel.Api");
            
            var validAudience = configuration["IdpSetting:ClientId"] ?? "Club.Channel.Api";
            
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidIssuer = validIssuer,
                ValidAudience = validAudience
            };

            // If using Memory provider, set up custom signing key resolver based on nameid/client_id claim
            if (isMemoryProvider)
            {
                var memorySecretKey = Encoding.UTF8.GetBytes("MemoryTokenService-SecretKey-For-Development-Only-Change-In-Production");
                var memorySigningKey = new SymmetricSecurityKey(memorySecretKey);
                
                // Set IssuerSigningKey directly for Memory provider
                tokenValidationParameters.IssuerSigningKey = memorySigningKey;
                
                // Also set IssuerSigningKeyResolver as fallback (in case Authority is set and tries to fetch keys)
                tokenValidationParameters.IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
                {
                    // For Memory provider, we use a fixed key
                    // Extract nameid or client_id from token to verify it matches
                    if (securityToken is System.IdentityModel.Tokens.Jwt.JwtSecurityToken jwtToken)
                    {
                        var nameid = jwtToken.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                        var clientId = jwtToken.Claims.FirstOrDefault(c => c.Type == "client_id")?.Value;
                        
                        // If we have nameid or client_id, return the memory signing key
                        if (!string.IsNullOrEmpty(nameid) || !string.IsNullOrEmpty(clientId))
                        {
                            return new[] { memorySigningKey };
                        }
                    }
                    
                    // Fallback: return memory signing key for Memory provider
                    return new[] { memorySigningKey };
                };
            }

            options.TokenValidationParameters = tokenValidationParameters;

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chat"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                },

                OnAuthenticationFailed = context =>
                {
                    // Log authentication failures for debugging
                    var logger = context.HttpContext.RequestServices.GetService<ILogger<JwtBearerOptions>>();
                    logger?.LogError("JWT Authentication failed: {Exception}", context.Exception);
                    logger?.LogError("Failure message: {Message}", context.Exception?.Message);
                    return Task.CompletedTask;
                },

                OnTokenValidated = context =>
                {
                    var claims = context.Principal!.Claims.ToList();
                    var realmRoles = claims.FirstOrDefault(c => c.Type == "realm_access");
                    var userId = claims.FirstOrDefault(c => c.Type == "userid")?.Value;

                    if (realmRoles != null)
                    {
                        var roles = System.Text.Json.JsonDocument.Parse(realmRoles.Value)
                            .RootElement.GetProperty("roles")
                            .EnumerateArray()
                            .Select(r => r.GetString());

                        var claimsIdentity = context.Principal.Identity as System.Security.Claims.ClaimsIdentity;
                        claimsIdentity?.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId!));
                        foreach (var role in roles)
                        {
                            claimsIdentity?.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role!));
                        }
                    }

                    return Task.CompletedTask;
                },
            };
        });
        return authenticationBuilder;
    }
    
    public static IServiceCollection AddNeoAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTransient<AuthTokenHandler>();

        services.AddHttpClient<IdpClientCredentialService>(option =>
            option.BaseAddress = new Uri(configuration["IdpSetting:BaseUrl"]!));

		var tokenProvider = configuration["IdpSetting:TokenProvider"] ?? "Memory";
        if (tokenProvider.Equals("Keycloak", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IIdpService, KeycloakIdpService>();
            services.AddHttpClient<IIdpService, KeycloakIdpService>(option =>
                option.BaseAddress = new Uri(configuration["IdpSetting:BaseUrl"]!))
                 .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                 {
                     ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
                 })
                .AddHttpMessageHandler<AuthTokenHandler>();
            services.AddHttpClient("client-credential-token", option =>
                  option.BaseAddress = new Uri(configuration["IdpSetting:BaseUrl"]!))
                 .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                 {
                     ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
                 });

            services.AddHttpClient<IAdminIdpService, AdminIdpService>(option =>
                option.BaseAddress = new Uri(configuration["IdpSetting:BaseUrl"]!));
        }
		else
		{
			services.AddSingleton<IIdpService, MemoryIdpService>();
			services.AddSingleton<IAdminIdpService, MemoryAdminIdpService>();
		}

		services.AddHttpClient<IRecaptchaService, RecaptchaService>(option =>
            option.BaseAddress = new Uri(configuration["Recaptcha:BaseUrl"]!));

        return services;
    }
}
