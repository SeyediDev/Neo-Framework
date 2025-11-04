using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Infrastructure.Features.Client;
public class AuthTokenHandler(IHttpContextAccessor contextAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
                        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Call the authentication service to get an access token

        var idpService =
               contextAccessor?.HttpContext?.RequestServices.GetRequiredService<IdpClientCredentialService>();

        var model = await idpService!.GetClientCredentialToken(cancellationToken);

        // Set the authorization header with the access token
        //TODO if(model is null) throw exeption

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model?.access_token);
        return await base.SendAsync(request, cancellationToken);
    }
}
