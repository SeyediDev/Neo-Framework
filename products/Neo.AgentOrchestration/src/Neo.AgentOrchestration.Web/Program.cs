using Neo.AgentOrchestration.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddRazorPages();
var apiUrl = builder.Configuration["OrchestrationApi:BaseUrl"];
if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiUri) ||
    apiUri.Scheme is not ("https" or "http") ||
    (apiUri.Scheme == "http" && !apiUri.IsLoopback))
    throw new InvalidOperationException("OrchestrationApi:BaseUrl requires HTTPS, or loopback HTTP for local use.");
builder.Services.AddHttpClient<OrchestrationClient>(c =>
{
    c.BaseAddress = apiUri;
    c.Timeout = TimeSpan.FromSeconds(5);
});
var app = builder.Build();
app.UseStaticFiles();
app.MapRazorPages();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
app.Run();

public partial class WebHost;
