using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Neo.FanasaIdentity.Application;

namespace Neo.FanasaIdentity.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFanasaIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(FanasaIdentityOptions.SectionName).Get<FanasaIdentityOptions>() ?? new();
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IIdentityRegistry, IdentityRegistry>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.Authority = options.Authority;
            jwt.Audience = options.Audience;
            jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "preferred_username",
                RoleClaimType = "human_role"
            };
        });
        services.AddAuthorization();
        return services;
    }
}
