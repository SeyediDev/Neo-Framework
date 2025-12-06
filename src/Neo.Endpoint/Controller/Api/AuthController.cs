using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Neo.Application.Features.Auth.Commands;
using Neo.Domain.Features.Client.Dto;
using Neo.Endpoint.Controller.Base;

namespace Neo.Endpoint.Controller.Api;

/// <summary>
/// کنترلر احراز هویت OAuth 2.0
/// </summary>
[VersionRoute("auth")]
[ApiVersion("1")]
[Tags("auth")]
[Produces("application/json")]
public class AuthController(ILogger<AuthController> logger) 
    : AppControllerBase
{
    /// <summary>
    /// دریافت توکن OAuth 2.0 با استفاده از Client Credentials Flow
    /// </summary>
    /// <remarks>
    /// این endpoint برای دریافت access token با استفاده از OAuth 2.0 Client Credentials Flow استفاده می‌شود.
    /// 
    /// **نکات مهم:**
    /// - grant_type باید "client_credentials" باشد
    /// - client_id و client_secret الزامی هستند
    /// - توکن دریافتی باید در header Authorization با فرمت "Bearer {token}" ارسال شود
    /// - توکن برای مدت زمان مشخصی (expires_in) معتبر است
    /// 
    /// **مثال درخواست:**
    /// ```
    /// POST /api/v1/auth/token
    /// Content-Type: application/x-www-form-urlencoded
    /// 
    /// grant_type=client_credentials&client_id=bajet-client&client_secret=bajet-secret
    /// ```
    /// </remarks>
    /// <param name="grant_type">نوع grant (باید "client_credentials" باشد)</param>
    /// <param name="client_id">شناسه کلاینت</param>
    /// <param name="client_secret">رمز کلاینت</param>
    /// <param name="scope">دامنه دسترسی (اختیاری)</param>
    /// <returns>Token response شامل access_token و expires_in</returns>
    /// <response code="200">توکن با موفقیت دریافت شد</response>
    /// <response code="401">اعتبارسنجی ناموفق بود</response>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<Results<Ok<TokenResponseDto>, UnauthorizedHttpResult>> GetToken(
        [FromForm] string grant_type,
        [FromForm] string client_id,
        [FromForm] string client_secret,
        [FromForm] string? scope = null)
    {
        //TODO
        grant_type = "client_credentials";

		if (grant_type != "client_credentials")
        {
            logger.LogWarning("Invalid grant_type: {GrantType}", grant_type);
            return TypedResults.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(client_id) || string.IsNullOrWhiteSpace(client_secret))
        {
            logger.LogWarning("Missing client_id or client_secret");
            return TypedResults.Unauthorized();
        }

        try
        {
            var command = new GetTokenCommand(client_id, client_secret);
            var tokenResponse = await Sender.Send(command);

            logger.LogInformation("Token issued for client {ClientId}", client_id);

            return TypedResults.Ok(tokenResponse);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Authentication failed for client {ClientId}", client_id);
            return TypedResults.Unauthorized();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating token for client {ClientId}", client_id);
            return TypedResults.Unauthorized();
        }
    }
}