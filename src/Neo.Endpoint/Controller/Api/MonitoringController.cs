using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Neo.Endpoint.Controller.Base;
using Swashbuckle.AspNetCore.Annotations;

namespace Neo.Endpoint.Controller.Api;

/// <summary>
/// کنترلر مانیتورینگ سیستم
/// این کنترلر در تمام API ها به صورت مشترک استفاده می‌شود
/// </summary>
[ApiController]
[VersionRoute("monitoring")]
[ApiVersion("1")]
[Tags("monitoring")]
[ApiExplorerSettings(IgnoreApi = true)] // مخفی کردن از Swagger
public sealed class MonitoringController : AppControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MonitoringController> _logger;

    public MonitoringController(IConfiguration configuration, ILogger<MonitoringController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// صفحه مانیتورینگ - Redirect به پنل ادمین
    /// </summary>
    /// <remarks>
    /// این endpoint به صفحه مانیتورینگ در پنل ادمین redirect می‌کند.
    /// لاگ‌های این API در پنل ادمین قابل مشاهده است.
    /// </remarks>
    /// <returns>Redirect به صفحه مانیتورینگ</returns>
    /// <response code="302">Redirect به صفحه مانیتورینگ</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [AllowAnonymous]
    public IActionResult Index()
    {
        var adminPanelUrl = _configuration["AdminPanel:BaseUrl"] ?? "http://localhost:5000";
        var monitoringUrl = $"{adminPanelUrl.TrimEnd('/')}/Monitoring";
        
        _logger.LogInformation("Redirecting to monitoring dashboard: {MonitoringUrl}", monitoringUrl);
        
        return Redirect(monitoringUrl);
    }

    /// <summary>
    /// اطلاعات API برای مانیتورینگ
    /// </summary>
    /// <remarks>
    /// این endpoint اطلاعات مربوط به API را برای نمایش در مانیتورینگ برمی‌گرداند.
    /// </remarks>
    /// <returns>اطلاعات API</returns>
    /// <response code="200">اطلاعات API</response>
    [HttpGet("info")]
    [ProducesResponseType(typeof(MonitoringApiInfo), StatusCodes.Status200OK)]
    [AllowAnonymous]
    public IActionResult GetApiInfo()
    {
        // دریافت نام API از تنظیمات یا از ApplicationName
        var apiName = _configuration["TelemetryOptions:ApplicationName"] 
            ?? _configuration["ApplicationName"] 
            ?? "Neo API";
        
        var apiVersion = _configuration["TelemetryOptions:ApplicationVersion"] 
            ?? _configuration["ApplicationVersion"] 
            ?? "1.0.0";
        
        var description = _configuration["TelemetryOptions:Description"] 
            ?? _configuration["ApplicationDescription"] 
            ?? "API سیستم";

        var apiInfo = new MonitoringApiInfo
        {
            ApiName = apiName,
            ApiVersion = apiVersion,
            MonitoringApiUrl = _configuration["TelemetryOptions:MonitoringApiUrl"] ?? "http://localhost:5000",
            LogsAvailable = true,
            Description = description
        };

        return Ok(apiInfo);
    }
}

/// <summary>
/// اطلاعات API برای مانیتورینگ
/// </summary>
public sealed record MonitoringApiInfo
{
    public string ApiName { get; init; } = null!;
    public string ApiVersion { get; init; } = null!;
    public string MonitoringApiUrl { get; init; } = null!;
    public bool LogsAvailable { get; init; }
    public string Description { get; init; } = null!;
}

