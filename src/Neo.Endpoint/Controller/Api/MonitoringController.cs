using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo.Domain.Features.Client;

namespace Neo.Endpoint.Controller.Api;

/// <summary>
/// کنترلر مانیتورینگ سیستم
/// این کنترلر در تمام API ها به صورت مشترک استفاده می‌شود
/// هر API می‌تواند در مسیر /Monitoring مانیتورینگ خود را نمایش دهد
/// </summary>
[Route("api/monitoring")]
[ApiExplorerSettings(IgnoreApi = true)] // مخفی کردن از Swagger
[Authorize] // احراز هویت اجباری برای دسترسی به مانیتورینگ
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
        // بررسی احراز هویت - اگر کاربر احراز نشده، به صفحه login هدایت می‌شود
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            _logger.LogWarning("Unauthenticated access attempt to monitoring dashboard");
            return Redirect("/Monitoring/Login?returnUrl=" + Uri.EscapeDataString("/Monitoring"));
        }
        
        _logger.LogInformation("Monitoring dashboard accessed by user {User}", User.Identity?.Name);
        SetPersianCulture();
        return View("Index");
    }
    
    /// <summary>
    /// صفحه ورود برای مانیتورینگ
    /// </summary>
    [HttpGet("~/Monitoring/Login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        // اگر کاربر قبلاً احراز هویت شده، به صفحه مانیتورینگ هدایت می‌شود
        if (User.Identity?.IsAuthenticated ?? false)
        {
            return Redirect(returnUrl ?? "/Monitoring");
        }
        
        ViewBag.ReturnUrl = returnUrl ?? "/Monitoring";
        SetPersianCulture();
        return View("Login");
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
            MonitoringApiUrl = _configuration["TelemetryOptions:MonitoringApiUrl"] ?? "http://localhost:5000",
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
    /// Recent logs endpoint
    /// </summary>
    [HttpGet("logs/recent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRecentLogs([FromQuery] int limit = 50, [FromQuery] int? minLevel = null)
    {
        // Stub implementation - returns empty array
        // TODO: Implement actual log retrieval
        return Ok(new object[0]);
    }

    /// <summary>
    /// All metrics endpoint
    /// </summary>
    [HttpGet("metrics/all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAllMetrics()
    {
        // Stub implementation - returns empty array
        // TODO: Implement actual metrics collection
        return Ok(new object[0]);
    }

    /// <summary>
    /// Metric timeseries endpoint
    /// </summary>
    [HttpGet("metrics/{metricName}/timeseries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetMetricTimeseries(string metricName, [FromQuery] string from)
    {
        // Stub implementation - returns empty array
        // TODO: Implement actual timeseries data
        return Ok(new object[0]);
    }

    /// <summary>
    /// Recent traces endpoint
    /// </summary>
    [HttpGet("traces/recent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRecentTraces([FromQuery] int limit = 50)
    {
        // Stub implementation - returns empty array
        // TODO: Implement actual trace retrieval
        return Ok(new object[0]);
    }

    /// <summary>
    /// Trace statistics endpoint
    /// </summary>
    [HttpGet("traces/stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetTraceStats()
    {
        // Stub implementation
        var stats = new
        {
            TotalTraces = 0,
            AverageDuration = 0.0,
            ErrorRate = 0.0
        };
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

