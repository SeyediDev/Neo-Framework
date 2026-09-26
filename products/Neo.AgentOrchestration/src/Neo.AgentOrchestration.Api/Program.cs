using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Neo.AgentOrchestration.Application;
using Neo.Endpoint;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var mvc = builder.Services.AddControllers();
builder.Services.AddNeoControllerServices(builder.Configuration, "Neo Agent Orchestration", existingMvcBuilder: mvc);
// The host exposes its own API surface; optional framework monitoring controllers
// require a separate policy and are not implicitly published by this product.
mvc.PartManager.ApplicationParts.Clear();
mvc.PartManager.ApplicationParts.Add(new AssemblyPart(typeof(ApiHost).Assembly));
builder.Services.AddMediatR(c =>
{
    c.RegisterServicesFromAssemblyContaining<GetProductInfo>();
    // Work commands require a transactional store and scoped membership first.
    // Keep foundation startup valid without silently selecting an in-memory store.
    c.TypeEvaluator = type => type == typeof(GetProductInfoHandler);
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = builder.Configuration["Authentication:Authority"];
    o.Audience = builder.Configuration["Authentication:Audience"];
    o.MapInboundClaims = false;
});
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing") &&
    (string.IsNullOrWhiteSpace(builder.Configuration["Authentication:Authority"]) ||
     string.IsNullOrWhiteSpace(builder.Configuration["Authentication:Audience"])))
    throw new InvalidOperationException("Configure Authentication:Authority and Authentication:Audience.");

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.UseOpenApi();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class ApiHost;
