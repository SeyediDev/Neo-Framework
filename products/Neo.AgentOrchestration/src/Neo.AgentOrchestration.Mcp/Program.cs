using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Neo.AgentOrchestration.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
McpConnection connection;
try { connection = McpConnection.Load(builder.Configuration); }
catch (ArgumentException)
{
    Console.Error.WriteLine("Invalid NeoMcp configuration. Set API URL, organization, workspace, chat and token secret reference; see docs/MCP.md.");
    return 2;
}
builder.Services.AddSingleton(connection);
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 4 * 1024 * 1024
});
builder.Services.AddSingleton<McpApiClient>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<WorkTools>(McpJson.Options);
await builder.Build().RunAsync();
return 0;
