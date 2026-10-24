using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
public sealed class PolicyWorkbenchModel(IWebHostEnvironment environment, IAntiforgery antiforgery) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "Workbench", "index.html");
        if (!System.IO.File.Exists(path))
            return new ContentResult { StatusCode = StatusCodes.Status503ServiceUnavailable, ContentType = "text/html; charset=utf-8",
                Content = "<!doctype html><html lang=\"fa\" dir=\"rtl\"><meta charset=\"utf-8\"><p>فایل رابط کارگاه در بستهٔ استقرار موجود نیست.</p></html>" };
        var html = await System.IO.File.ReadAllTextAsync(path, cancellationToken);
        // Absolute asset URLs retain /PolicyWorkbench as the document URL for hash navigation and SVG fragments.
        foreach (var asset in new[] { "assets/", "styles.css", "workbench.css", "app.js", "workbench.js" })
            foreach (var attribute in new[] { "href", "src" })
                html = html.Replace(attribute + "=\"" + asset, attribute + "=\"/workbench/" + asset, StringComparison.Ordinal);
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (head < 0) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        html = html.Insert(head + "<head>".Length,
            "\n<meta name=\"wb-csrf\" content=\"" + HtmlEncoder.Default.Encode(token ?? "") + "\">\n");
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Vary = "Cookie";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.XFrameOptions = "SAMEORIGIN";
        Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; form-action 'self'";
        return Content(html, "text/html; charset=utf-8");
    }
}
