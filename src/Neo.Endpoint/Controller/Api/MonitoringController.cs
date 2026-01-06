using System.Globalization;
using System.Security.Claims;
using System.Threading;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo.Domain.Features.Client;
using Swashbuckle.AspNetCore.Annotations;

namespace Neo.Endpoint.Controller.Api;

/// <summary>
/// کنترلر مانیتورینگ سیستم
/// این کنترلر در تمام API ها به صورت مشترک استفاده می‌شود
/// هر API می‌تواند در مسیر /Monitoring مانیتورینگ خود را نمایش دهد
/// </summary>
[Route("Monitoring")]
[ApiExplorerSettings(IgnoreApi = true)] // مخفی کردن از Swagger
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
    [HttpGet]
    [HttpGet("Index")]
    [AllowAnonymous]
    public IActionResult Index()
    {
        _logger.LogInformation("Monitoring dashboard accessed");
        SetPersianCulture();
        return View("Index");
    }

    /// <summary>
    /// صفحه مانیتورینگ پیشرفته
    /// </summary>
    [HttpGet("Advanced")]
    [AllowAnonymous]
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

