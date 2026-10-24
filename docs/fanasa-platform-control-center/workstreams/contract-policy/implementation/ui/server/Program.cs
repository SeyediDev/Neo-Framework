using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fanasa.ContractPolicy.Workbench;

var builder = WebApplication.CreateBuilder(args);
var portText = Environment.GetEnvironmentVariable("WORKBENCH_PORT") ?? "4187";
if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535)
    throw new InvalidOperationException("WORKBENCH_PORT must be a port from 1024 to 65535.");
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32768);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddSingleton(new WorkbenchStore(Environment.GetEnvironmentVariable("WORKBENCH_DATA_DIR")
    ?? builder.Configuration["Workbench:DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data")));
var allowedOrigin = Environment.GetEnvironmentVariable("WORKBENCH_ORIGIN");
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (context.Request.Method == "POST")
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin != "http://127.0.0.1:4186" && origin != "http://localhost:4186"
            && origin != "http://127.0.0.1:4187" && origin != "http://localhost:4187"
            && (string.IsNullOrEmpty(allowedOrigin) || origin != allowedOrigin))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "origin-denied", message = "A configured workbench origin is required." });
            return;
        }
        if (!context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = 415;
            await context.Response.WriteAsJsonAsync(new { error = "json-required", message = "JSON input is required." });
            return;
        }
    }
    try { await next(context); }
    catch (WorkbenchError error)
    {
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new { error = error.Code, message = error.Message, revision = error.Revision });
    }
    catch (RegexMatchTimeoutException)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "validation-timeout", message = "اعتبارسنجی در زمان مجاز تکمیل نشد؛ وضعیت را تازه‌سازی و دوباره بررسی کنید." });
    }
});
app.MapGet("/api/workbench", (WorkbenchStore store) => store.View());
app.MapGet("/api/workbench/history", (WorkbenchStore store) => store.History());
app.MapGet("/api/workbench/export", (WorkbenchStore store) => Results.Json(store.Export()));
app.MapGet("/api/workbench/readiness", (WorkbenchStore store) => store.Readiness());
app.MapPost("/api/workbench/validate", (ValidateConfigurationCommand command, WorkbenchStore store) => store.ValidateConfiguration(command));
app.MapPost("/api/workbench/draft", (DraftCommand command, WorkbenchStore store) => store.Draft(command));
app.MapPost("/api/workbench/publish", (PublishCommand command, WorkbenchStore store) => store.Publish(command));
app.MapPost("/api/workbench/retire", (PolicyCommand command, WorkbenchStore store) => store.Retire(command));
app.MapPost("/api/workbench/bind", (PolicyCommand command, WorkbenchStore store) => store.Bind(command));
app.MapPost("/api/workbench/evaluate", (EvaluateCommand command, WorkbenchStore store) => store.Evaluate(command));
app.MapPost("/api/workbench/execute", (ExecuteCommand command, WorkbenchStore store) => store.Execute(command));
app.MapPost("/api/workbench/reset", (RevisionCommand command, WorkbenchStore store) => store.Reset(command));
app.MapGet("/health", (WorkbenchStore store) =>
{
    var storage = store.Integrity();
    return Results.Json(new { status = storage.Healthy ? "ready" : "invalid", mode = "isolated-local-workbench", storage },
        statusCode: storage.Healthy ? 200 : 503);
});
app.Run();
