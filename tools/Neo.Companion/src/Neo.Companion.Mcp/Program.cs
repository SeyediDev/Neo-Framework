using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Neo.Companion.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(new CompanionCatalog(AppContext.BaseDirectory,
    Environment.GetEnvironmentVariable("NEO_PROJECT_ROOT")));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<NeoTools>();
await builder.Build().RunAsync();
