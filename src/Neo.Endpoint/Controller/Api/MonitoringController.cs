using System.Globalization;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo.Domain.Features.Client;
using Neo.Endpoint.Infrastructure;
using System.Text.Json;

namespace Neo.Endpoint.Controller.Api;

/// <summary>
/// کنترلر مانیتورینگ سیستم
/// این کنترلر در تمام API ها به صورت مشترک استفاده می‌شود
/// هر API می‌تواند در مسیر /Monitoring مانیتورینگ خود را نمایش دهد
/// </summary>
[Route("api/monitoring")]
[ApiExplorerSettings(IgnoreApi = true)] // مخفی کردن از Swagger
[AllowAnonymous] // فعلاً احراز هویت غیرفعال است
public sealed class MonitoringController : Microsoft.AspNetCore.Mvc.Controller
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MonitoringController> _logger;

    public MonitoringController(IConfiguration configuration, ILogger<MonitoringController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// صفحه مانیتورینگ - نمایش داشبورد مانیتورینگ
    /// </summary>
    /// <remarks>
    /// این endpoint صفحه مانیتورینگ را در خود API نمایش می‌دهد.
    /// هر API می‌تواند مانیتورینگ خود را در مسیر /Monitoring مشاهده کند.
    /// </remarks>
    /// <returns>View مانیتورینگ</returns>
    [HttpGet("~/Monitoring")]
    [HttpGet("~/Monitoring/Index")]
    public IActionResult Index()
    {
        _logger.LogInformation("Monitoring dashboard accessed");
        SetPersianCulture();
        
        // تنظیم نام API برای View
        var apiName = _configuration["TelemetryOptions:ApplicationName"] 
            ?? _configuration["ApplicationName"] 
            ?? "API";
        ViewBag.ApiName = apiName;
        ViewData["Title"] = $"مانیتورینگ {apiName}";
        
        return View("Index");
    }
    

    /// <summary>
    /// صفحه مانیتورینگ پیشرفته
    /// </summary>
    [HttpGet("~/Monitoring/Advanced")]
    public IActionResult Advanced()
    {
        _logger.LogInformation("Advanced monitoring dashboard accessed");
        SetPersianCulture();
        return View("Advanced");
    }
    
    /// <summary>
    /// تنظیم Culture به فارسی برای صفحه مانیتورینگ
    /// </summary>
    private void SetPersianCulture()
    {
        ViewBag.PagePackId = "/Monitoring/Index";
        ViewBag.user = GetUser(User);

		try
        {
            var persianCulture = new CultureInfo("fa-IR");
            CultureInfo.CurrentCulture = persianCulture;
            CultureInfo.CurrentUICulture = persianCulture;
            
            // تنظیم Culture برای Thread جاری
            Thread.CurrentThread.CurrentCulture = persianCulture;
            Thread.CurrentThread.CurrentUICulture = persianCulture;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set Persian culture, using default culture");
        }
		// تنظیم CSP header برای جلوگیری از خطاهای BrowserLink
		Response.Headers.Append("Content-Security-Policy",
			"default-src 'self'; " +
			"script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
			"style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
			"connect-src 'self' ws://localhost:* http://localhost:* ws://127.0.0.1:* http://127.0.0.1:* ws://* http://* https://*; " +
			"img-src 'self' data: https:; " +
			"font-src 'self' data: https://cdn.jsdelivr.net https://fonts.googleapis.com https://fonts.gstatic.com;");
	}
	
    private object GetUser(ClaimsPrincipal claimsPrincipal)
	{
		if (claimsPrincipal.Identity is null || !claimsPrincipal.Identity.IsAuthenticated)
		{
            return GenerateUser(claimsPrincipal);
		}
		IRequesterUser requesterUser = HttpContext.RequestServices.GetRequiredService<IRequesterUser>();
		var userProperty = requesterUser.GetProperty("IdentityUser");
		if (userProperty != null)
		{
			return userProperty;
		}
		return GenerateUser(claimsPrincipal);
	}

    private static object GenerateUser(ClaimsPrincipal? claimsPrincipal)
    {
		var claims = claimsPrincipal?.Claims ?? Enumerable.Empty<Claim>();
		var idClaim = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier) 
			?? claims.FirstOrDefault(c => c.Type == "sub");
		var nameClaim = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name) 
			?? claims.FirstOrDefault(c => c.Type == "name");
		
		string userName = (claimsPrincipal != null ? GetPrincipalName(claimsPrincipal) : null)
			?? nameClaim?.Value 
			?? claimsPrincipal?.Identity?.Name 
			?? "Anonymous User";
		string userId = idClaim?.Value 
			?? claimsPrincipal?.Identity?.Name 
			?? Guid.NewGuid().ToString();
		
		// ایجاد یک anonymous object که فیلدهای مورد نیاز View را دارد
		return new
		{
			Id = userId,
			UserName = userName,
			FirstName = claims.FirstOrDefault(c => c.Type == "FirstName")?.Value,
			LastName = claims.FirstOrDefault(c => c.Type == "LastName")?.Value,
			NationalNumber = claims.FirstOrDefault(c => c.Type == ClaimTypes.Sid)?.Value,
			MobileNo = claims.FirstOrDefault(c => c.Type == ClaimTypes.MobilePhone)?.Value,
			PhoneNumber = claims.FirstOrDefault(c => c.Type == ClaimTypes.OtherPhone)?.Value,
			Email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
				?? claims.FirstOrDefault(c => c.Type == "email")?.Value,
		};
	}

	private static string? GetPrincipalName(ClaimsPrincipal? principal)
	{
		string? name = principal?.Identity?.Name;
		if (string.IsNullOrEmpty(name))
			return null;
		string[] nameArray = name.Split('\\');
		return nameArray.Length < 1 ? null : nameArray[^1];
	}

	/// <summary>
	/// اطلاعات API برای مانیتورینگ
	/// </summary>
	/// <remarks>
	/// این endpoint اطلاعات مربوط به API را برای نمایش در مانیتورینگ برمی‌گرداند.
	/// </remarks>
	/// <returns>اطلاعات API</returns>
	/// <response code="200">اطلاعات API</response>
	[HttpGet("api/info")]
    [ProducesResponseType(typeof(MonitoringApiInfo), StatusCodes.Status200OK)]
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
            MonitoringApiUrl = Request.Scheme + "://" + Request.Host,
            LogsAvailable = true,
            Description = description
        };

        return Ok(apiInfo);
    }

    /// <summary>
    /// Dashboard data endpoint
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDashboard()
    {
        // Stub implementation - returns empty data structure
        // TODO: Implement actual dashboard data collection
        var dashboard = new
        {
            System = new
            {
                CpuUsagePercent = 0.0,
                MemoryUsagePercent = 0.0,
                MemoryUsedBytes = 0L,
                MemoryTotalBytes = 0L
            },
            Application = new
            {
                RequestsPerSecond = 0.0,
                TotalRequests = 0L,
                AverageResponseTimeMs = 0.0,
                ActiveRequests = 0,
                SuccessRate = 100.0,
                FailedRequests = 0L
            }
        };
        return Ok(dashboard);
    }

    /// <summary>
    /// Health status endpoint
    /// </summary>
    [HttpGet("dashboard/health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHealth()
    {
        // Stub implementation
        var health = new { Status = "Healthy" };
        return Ok(health);
    }

    /// <summary>
    /// دریافت لاگ‌ها از OTLP (برای Serilog Sink)
    /// </summary>
    [HttpPost("logs/otlp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReceiveLogsOtlp()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            
            if (string.IsNullOrWhiteSpace(body))
                return BadRequest("Empty request body");
            
            var logEntry = JsonSerializer.Deserialize<object>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (logEntry != null)
            {
                MonitoringDataStore.AddLog(logEntry);
            }
            
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error receiving log from OTLP");
            return BadRequest("Invalid log format");
        }
    }

    /// <summary>
    /// Recent logs endpoint
    /// </summary>
    [HttpGet("logs/recent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRecentLogs(
        [FromQuery] int limit = 50, 
        [FromQuery] int? minLevel = null,
        [FromQuery] string? correlationId = null,
        [FromQuery] string? source = null,
        [FromQuery] string? textSearch = null)
    {
        var logs = MonitoringDataStore.GetRecentLogs(limit, minLevel, correlationId, source, textSearch);
        return Ok(logs);
    }

    /// <summary>
    /// All metrics endpoint
    /// </summary>
    [HttpGet("metrics/all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAllMetrics()
    {
        var metrics = MonitoringDataStore.GetAllMetrics();
        return Ok(metrics);
    }

    /// <summary>
    /// Metric timeseries endpoint
    /// </summary>
    [HttpGet("metrics/{metricName}/timeseries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetMetricTimeseries(string metricName, [FromQuery] string from)
    {
        // فعلاً stub - می‌تواند در آینده از OpenTelemetry استفاده کند
        var metric = MonitoringDataStore.GetMetric(metricName);
        if (metric != null)
        {
            // برگرداندن داده‌های timeseries ساده
            return Ok(new[]
            {
                new { Timestamp = DateTime.UtcNow, Value = 0.0 }
            });
        }
        return Ok(new object[0]);
    }

    /// <summary>
    /// Recent traces endpoint
    /// </summary>
    [HttpGet("traces/recent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRecentTraces(
        [FromQuery] int limit = 50,
        [FromQuery] string? from = null,
        [FromQuery] string? serviceName = null,
        [FromQuery] string? kind = null,
        [FromQuery] int? status = null)
    {
        DateTime? fromDate = null;
        if (!string.IsNullOrWhiteSpace(from) && DateTime.TryParse(from, out var parsedDate))
        {
            fromDate = parsedDate;
        }
        
        var traces = MonitoringDataStore.GetRecentTraces(limit, fromDate, serviceName, kind, status);
        return Ok(traces);
    }

    /// <summary>
    /// Trace statistics endpoint
    /// </summary>
    [HttpGet("traces/stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetTraceStats()
    {
        var stats = MonitoringDataStore.GetTraceStats();
        return Ok(stats);
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

