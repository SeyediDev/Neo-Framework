using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Neo.AgentOrchestration.Application;
using Neo.AgentOrchestration.Api;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Neo.Endpoint;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddExceptionHandler<OrchestrationApiExceptions>();
var mvc = builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddNeoControllerServices(builder.Configuration, "Neo Agent Orchestration", existingMvcBuilder: mvc);
// Neo permits custom validation pipelines; this host uses MVC body validation.
builder.Services.Configure<ApiBehaviorOptions>(o => o.SuppressModelStateInvalidFilter = false);
// The host exposes its own API surface; optional framework monitoring controllers
// require a separate policy and are not implicitly published by this product.
mvc.PartManager.ApplicationParts.Clear();
mvc.PartManager.ApplicationParts.Add(new AssemblyPart(typeof(ApiHost).Assembly));
builder.Services.AddMediatR(c =>
{
    c.RegisterServicesFromAssemblyContaining<GetProductInfo>();
});
// Resolve the final composed configuration, including host-added providers.
// An early branch can permanently select the wrong store before Build applies them.
builder.Services.AddOrchestrationSql(provider => OrchestrationApiStorage.Connection(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddHttpHarness();
builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HarnessAuthentication>(HarnessAuthentication.Name, _ => { });
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = builder.Configuration["Authentication:Authority"];
    o.Audience = builder.Configuration["Authentication:Audience"];
    o.MapInboundClaims = false;
});
builder.Services.AddAuthorization(WorkspaceSecurity.AddPolicies);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
var app = builder.Build();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing") &&
    (string.IsNullOrWhiteSpace(app.Configuration["Authentication:Authority"]) ||
     string.IsNullOrWhiteSpace(app.Configuration["Authentication:Audience"])))
    throw new InvalidOperationException("Configure Authentication:Authority and Authentication:Audience.");
OrchestrationApiStorage.ValidateConfiguration(app.Configuration);
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
// Authentication runs first: even the generated contract requires a valid token.
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("OpenApi:Enabled")) app.UseOpenApi();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class ApiHost;
