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

namespace Neo.Infrastructure.Features.Client;

public static class DependencyInjection
{
    public static AuthenticationBuilder AddCandoAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var authenticationBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);
        authenticationBuilder.AddJwtBearer(options =>
        {
            options.Authority = configuration["IdpSetting:Authority"];
            options.Audience = configuration["IdpSetting:ClientId"];
            options.RequireHttpsMetadata = false;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidIssuer = configuration["IdpSetting:Authority"],
                ValidAudience = configuration["IdpSetting:ClientId"]
            };

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
    public static IServiceCollection AddCandoAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTransient<AuthTokenHandler>();

        services.AddHttpClient<IdpClientCredentialService>(option =>
            option.BaseAddress = new Uri(configuration["IdpSetting:BaseUrl"]!));

        services.AddHttpClient<IIdpService, IdpService>(option =>
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

        services.AddHttpClient<IRecaptchaService, RecaptchaService>(option =>
            option.BaseAddress = new Uri(configuration["Recaptcha:BaseUrl"]!));

        return services;
    }
}
