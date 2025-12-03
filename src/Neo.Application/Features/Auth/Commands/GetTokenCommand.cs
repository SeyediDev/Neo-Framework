using Neo.Domain.Features.Client;
using Neo.Domain.Features.Client.Dto;

namespace Neo.Application.Features.Auth.Commands;

/// <summary>
/// دستور دریافت توکن OAuth 2.0
/// </summary>
public sealed record GetTokenCommand(
    string ClientId,
    string ClientSecret
) : IRequest<TokenResponseDto>;

internal sealed class GetTokenCommandHandler(
	IIdpService tokenService
) : IRequestHandler<GetTokenCommand, TokenResponseDto>
{
    public async Task<TokenResponseDto> Handle(GetTokenCommand request, CancellationToken cancellationToken)
    {
        var rs = await tokenService.GetClientCredentialTokenAsync(request.ClientId, request.ClientSecret, cancellationToken);
        if (rs != null)
        {

            return new TokenResponseDto() 
            { 
                access_token = rs.access_token, 
                refresh_token = rs.access_token,
                expires_in = rs.expires_in,
                refresh_expires_in = rs.expires_in
			};
        }
        return null!;
    }
}