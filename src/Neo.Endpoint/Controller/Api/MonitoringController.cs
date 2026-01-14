using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class MonitoringController(IConfiguration configuration, ILogger<MonitoringController> logger) 
    : Microsoft.AspNetCore.Mvc.Controller
{

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
        logger.LogInformation("Monitoring dashboard accessed");
        SetPersianCulture();
        
        // تنظیم نام API برای View
        var apiName = configuration["TelemetryOptions:ApplicationName"] 
            ?? configuration["ApplicationName"] 
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
        logger.LogInformation("Advanced monitoring dashboard accessed");
        SetPersianCulture();
        return View("Advanced");
    }
    
    /// <summary>
    /// صفحه مانیتورینگ میکروفرانت (نسخه موقت برای تست)
    /// </summary>
    [HttpGet("~/Monitoring/Index2")]
    public IActionResult Index2()
    {
        logger.LogInformation("Micro-frontend monitoring dashboard accessed");
        SetPersianCulture();
        
        var apiName = configuration["TelemetryOptions:ApplicationName"] 
            ?? configuration["ApplicationName"] 
            ?? "API";
        ViewBag.ApiName = apiName;
        ViewData["Title"] = $"مانیتورینگ {apiName} (میکروفرانت)";
        
        return View("Index2");
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
            logger.LogWarning(ex, "Failed to set Persian culture, using default culture");
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
        var apiName = configuration["TelemetryOptions:ApplicationName"] 
            ?? configuration["ApplicationName"] 
            ?? "Neo API";
        
        var apiVersion = configuration["TelemetryOptions:ApplicationVersion"] 
            ?? configuration["ApplicationVersion"] 
            ?? "1.0.0";
        
        var description = configuration["TelemetryOptions:Description"] 
            ?? configuration["ApplicationDescription"] 
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

    // Dashboard and health endpoints are now handled by DashboardController
    // [Route("api/monitoring/dashboard")] in Features/Monitoring/Controllers/DashboardController.cs

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
            logger.LogWarning(ex, "Error receiving log from OTLP");
            return BadRequest("Invalid log format");
        }
    }

    // Logs endpoints are now handled by LogsController
    // [Route("api/monitoring/logs")] in Features/Monitoring/Controllers/LogsController.cs
    
    // Metrics endpoints are now handled by MetricsController
    // [Route("api/monitoring/metrics")] in Features/Monitoring/Controllers/MetricsController.cs
    
    // Traces endpoints are now handled by TracesController
    // [Route("api/monitoring/traces")] in Features/Monitoring/Controllers/TracesController.cs
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