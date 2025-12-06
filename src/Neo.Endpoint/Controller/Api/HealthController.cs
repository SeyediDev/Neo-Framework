using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Neo.Endpoint.Controller.Base;
using Swashbuckle.AspNetCore.Annotations;

namespace Neo.Endpoint.Controller.Api;

/// <summary>
/// کنترلر سلامت سرویس
/// </summary>
[ApiController]
[VersionRoute("health")]
[ApiVersion("1")]
[Tags("health")]
[SwaggerTag("health", "تست سلامت سرویس")]
public sealed class HealthController : AppControllerBase
{
    /// <summary>
    /// PingPong بدون احراز هویت - برای تست سلامت Swagger
    /// </summary>
    /// <remarks>
    /// این endpoint برای تست سلامت Swagger و API استفاده می‌شود.
    /// نیاز به احراز هویت ندارد.
    /// </remarks>
    /// <returns>پیام PingPong</returns>
    /// <response code="200">سرویس سالم است</response>
    [HttpGet("ping")]
    [ProducesResponseType(typeof(PingPongResponse), StatusCodes.Status200OK)]
    [SwaggerOperation(Summary = "تست سلامت Swagger", Description = "این endpoint برای تست سلامت Swagger و API استفاده می‌شود")]
    public IActionResult Ping()
    {
        return Ok(new PingPongResponse
        {
            Message = "Pong",
            Timestamp = DateTime.UtcNow,
            Service = "Neo API",
            Version = "1.0.0"
        });
    }

    /// <summary>
    /// PingPong با احراز هویت - برای تست سلامت توکن
    /// </summary>
    /// <remarks>
    /// این endpoint برای تست سلامت توکن OAuth 2.0 استفاده می‌شود.
    /// نیاز به Bearer Token دارد.
    /// </remarks>
    /// <returns>پیام PingPong با اطلاعات توکن</returns>
    /// <response code="200">توکن معتبر است</response>
    /// <response code="401">توکن معتبر نیست</response>
    [HttpGet("ping/auth")]
    [ProducesResponseType(typeof(AuthPingPongResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [Authorize]
    [SwaggerOperation(Summary = "تست سلامت توکن", Description = "این endpoint برای تست سلامت توکن OAuth 2.0 استفاده می‌شود")]
    public IActionResult PingWithAuth()
    {
        var userId = User?.Identity?.Name ?? "Unknown";
        var claims = User?.Claims?.Select(c => new ClaimInfo { Type = c.Type, Value = c.Value }) ?? Enumerable.Empty<ClaimInfo>();

        return Ok(new AuthPingPongResponse
        {
            Message = "Pong (Authenticated)",
            Timestamp = DateTime.UtcNow,
            Service = "Neo API",
            Version = "1.0.0",
            UserId = userId,
            IsAuthenticated = true,
            Claims = claims
        });
    }
}

/// <summary>
/// پاسخ PingPong ساده
/// </summary>
public sealed record PingPongResponse
{
    public string Message { get; init; } = null!;
    public DateTime Timestamp { get; init; }
    public string Service { get; init; } = null!;
    public string Version { get; init; } = null!;
}

/// <summary>
/// پاسخ PingPong با احراز هویت
/// </summary>
public sealed record AuthPingPongResponse
{
    public string Message { get; init; } = null!;
    public DateTime Timestamp { get; init; }
    public string Service { get; init; } = null!;
    public string Version { get; init; } = null!;
    public string UserId { get; init; } = null!;
    public bool IsAuthenticated { get; init; }
    public IEnumerable<ClaimInfo> Claims { get; init; } = null!;
}

/// <summary>
/// اطلاعات Claim
/// </summary>
public sealed record ClaimInfo
{
    public string Type { get; init; } = null!;
    public string Value { get; init; } = null!;
}

