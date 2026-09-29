using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;

[CollectionDefinition("OperationalMcp", DisableParallelization = true)]
public sealed class OperationalMcpCollection;

[Collection("OperationalMcp")]
public sealed class OperationalMcpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Stdio_tools_execute_scoped_task_lifecycle_against_real_http_jwt_and_sql()
    {
        var f = await Fixture.Create();
        await using var api = new ApiFixture(clock: f.Clock, sql: f.Connection);
        api.Factory.UseKestrel(0);
        using var identity = api.Client(f.Scope);
        await using var mcp = await McpProcess.Start(api, identity, f.Scope);
        var catalog = await mcp.Call<WorkspaceCatalog>("neo_work_catalog");
        Assert.Contains(catalog.Roles, x => x.Id == f.Role.Id);
        var tools = (await mcp.Request("tools/list", new { })).GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(19, tools.Length);
        Assert.Contains(tools, x => x.GetProperty("name").GetString() == "neo_work_planning");
        Assert.True(tools.Single(x => x.GetProperty("name").GetString() == "neo_work_get").GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(tools.Single(x => x.GetProperty("name").GetString() == "neo_run_start").GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var startSchema = tools.Single(x => x.GetProperty("name").GetString() == "neo_run_start").GetProperty("inputSchema").GetRawText();
        Assert.Contains("allowExternalExecution", startSchema);
        var context = await mcp.Call<JsonElement>("neo_work_context");
        Assert.Equal(f.Scope.WorkspaceId, context.GetProperty("workspaceId").GetGuid());
        Assert.DoesNotContain(identity.DefaultRequestHeaders.Authorization!.Parameter!, context.GetRawText());

        var parent = await mcp.Call<WorkItemDetails>("neo_work_create", new { request = new CreateWorkItemRequest(f.Project.Id, "mcp-parent", "Parent", "mcp", "Original chat request") });
        parent = await mcp.Call<WorkItemDetails>("neo_work_planning", new { itemId = parent.Item.Id,
            request = new SetWorkItemPlanningRequest(parent.Item.Version, "UserStory", "Observable outcome") });
        Assert.Equal("UserStory", parent.Item.Type);
        Assert.Equal("Observable outcome", parent.Item.AcceptanceCriteria);
        var typed = await mcp.Call<WorkBoard>("neo_work_board", new { projectId = f.Project.Id, type = "UserStory" });
        Assert.Equal(parent.Item.Id, Assert.Single(typed.Items).Id);
        var child = await mcp.Call<WorkItemDetails>("neo_work_create", new { request = new CreateWorkItemRequest(f.Project.Id, "mcp-child", "Child", "api", ParentWorkItemId: parent.Item.Id, EstimatedSeconds: 100) });
        Assert.Single((await mcp.Call<WorkItemDetails>("neo_work_get", new { itemId = parent.Item.Id })).Children);
        child = await mcp.Call<WorkItemDetails>("neo_work_status", new { itemId = child.Item.Id, request = new ChangeStatusRequest(child.Item.Version, "Ready") });
        child = await mcp.Call<WorkItemDetails>("neo_work_claim", new { itemId = child.Item.Id, request = new ClaimWorkItemRequest(child.Item.Version, f.Role.Id, "test/mcp") });
        Assert.Equal("agent-a", child.Item.OwnerAgentId); Assert.Equal("mcp-test-chat", child.Item.OwnerChatId);
        var stale = child.Item.Version;
        child = await mcp.Call<WorkItemDetails>("neo_work_log", new { itemId = child.Item.Id, request = new AppendLogRequest(child.Item.Version, "Preserved follow-up context.") });
        await mcp.Error("neo_work_log", new { itemId = child.Item.Id, request = new AppendLogRequest(stale, "Stale mutation.") }, "api-409");
        f.Clock.Advance(20);
        child = await mcp.Call<WorkItemDetails>("neo_work_time", new { itemId = child.Item.Id, expectedVersion = child.Item.Version, start = false });
        Assert.Equal(20, child.Item.ElapsedSeconds); Assert.False(child.Item.IsTracking);
        child = await mcp.Call<WorkItemDetails>("neo_work_estimate", new { itemId = child.Item.Id, request = new SetEstimateRequest(child.Item.Version, 200) });
        Assert.Equal(10m, child.Item.BudgetUsedPercent);
        const string sha = "0123456789abcdef0123456789abcdef01234567"; // Fixture evidence only.
        child = await mcp.Call<WorkItemDetails>("neo_work_evidence", new { itemId = child.Item.Id, request = new AddEvidenceRequest(child.Item.Version, "Commit", sha, "NotApplicable") });
        child = await mcp.Call<WorkItemDetails>("neo_work_evidence", new { itemId = child.Item.Id, request = new AddEvidenceRequest(child.Item.Version, "Test", "mcp-fixture", "Passed", CommitSha: sha) });
        var board = await mcp.Call<WorkBoard>("neo_work_board", new { projectId = f.Project.Id, roleId = f.Role.Id, domain = "api" });
        Assert.Single(board.Items); Assert.Equal(20, board.Metrics.ElapsedSeconds);
        Assert.Empty((await mcp.Call<WorkBoard>("neo_work_board", new { domain = "other" })).Items);
        child = await mcp.Call<WorkItemDetails>("neo_work_status", new { itemId = child.Item.Id, request = new ChangeStatusRequest(child.Item.Version, "Blocked", "Fixture stopped.") });
        child = await mcp.Call<WorkItemDetails>("neo_work_archive", new { itemId = child.Item.Id, expectedVersion = child.Item.Version, archived = true });
        Assert.True(child.Item.IsArchived);
        child = await mcp.Call<WorkItemDetails>("neo_work_archive", new { itemId = child.Item.Id, expectedVersion = child.Item.Version, archived = false });
        Assert.False(child.Item.IsArchived); Assert.Equal(2, child.Evidence.Count);
        Assert.Contains(child.Logs, x => x.Message == "Preserved follow-up context." && x.ChatId == "mcp-test-chat");
        // New process, same issuer subject/chat: persisted context is recoverable.
        await using var resumed = await McpProcess.Start(api, identity, f.Scope);
        var saved = await resumed.Call<WorkItemDetails>("neo_work_get", new { itemId = child.Item.Id });
        Assert.Equal(child.Item.Version, saved.Item.Version); Assert.Equal(20, saved.Item.ElapsedSeconds);
        Assert.Equal("Original chat request", (await resumed.Call<WorkItemDetails>("neo_work_get", new { itemId = parent.Item.Id })).Item.Description);
        Assert.DoesNotContain(identity.DefaultRequestHeaders.Authorization.Parameter!, mcp.Stderr);
    }

    [Fact]
    public async Task Stdio_cannot_bypass_permissions_scope_owner_or_payload_contract()
    {
        var f = await Fixture.Create();
        await using var api = new ApiFixture(clock: f.Clock, sql: f.Connection);
        api.Factory.UseKestrel(0);
        using var owner = api.Client(f.Scope);
        await using var mcp = await McpProcess.Start(api, owner, f.Scope);
        await mcp.Error("neo_work_create", new { request = new { projectId = f.Project.Id, key = "spoof", title = "Spoof", domain = "api", agentId = "another" } });
        var item = await mcp.Call<WorkItemDetails>("neo_work_create", new { request = new CreateWorkItemRequest(f.Project.Id, "owned", "Owned", "api") });
        item = await mcp.Call<WorkItemDetails>("neo_work_status", new { itemId = item.Item.Id, request = new ChangeStatusRequest(item.Item.Version, "Ready") });
        item = await mcp.Call<WorkItemDetails>("neo_work_claim", new { itemId = item.Item.Id, request = new ClaimWorkItemRequest(item.Item.Version, f.Role.Id) });
        using var stranger = api.Client(f.Scope, subject: "another-subject");
        await using var other = await McpProcess.Start(api, stranger, f.Scope);
        await other.Error("neo_work_log", new { itemId = item.Item.Id, request = new AppendLogRequest(item.Item.Version, "Takeover") }, "api-409");
        using var reader = api.Client(f.Scope, ["read"]);
        await using var readOnly = await McpProcess.Start(api, reader, f.Scope);
        await readOnly.Call<WorkBoard>("neo_work_board");
        await readOnly.Error("neo_work_create", new { request = new CreateWorkItemRequest(f.Project.Id, "forbidden", "Forbidden", "api") }, "api-403");
        await using var foreign = await McpProcess.Start(api, owner, new(f.Scope.OrganizationId, Guid.NewGuid()));
        await foreign.Error("neo_work_board", new { }, "api-403");
        await mcp.Error("neo_work_board", new { take = 201 }, "api-400");
        await mcp.Error("neo_work_log", new { itemId = item.Item.Id, request = new AppendLogRequest(item.Item.Version, owner.DefaultRequestHeaders.Authorization!.Parameter!) }, "credential");
        var saved = await mcp.Call<WorkItemDetails>("neo_work_get", new { itemId = item.Item.Id });
        Assert.Equal(item.Item.Version, saved.Item.Version);
        Assert.DoesNotContain(saved.Logs, x => x.Message == "Takeover");
    }

    [Fact]
    public async Task Stdio_start_and_handoff_keep_idempotency_and_workflow_gates()
    {
        var f = await Fixture.Create(); var setup = await SqlRunTests.Configure(f, evidence: true);
        var item = await SqlRunTests.Ready(f);
        await using var api = new ApiFixture(clock: f.Clock, sql: f.Connection, simulation: true);
        api.Factory.UseKestrel(0);
        using var caller = api.Client(f.Scope, ["read", "write", "execute"]);
        await using var mcp = await McpProcess.Start(api, caller, f.Scope);
        var request = new StartAgentRunRequest(Guid.NewGuid(), item.Version, setup.Flow.Id, setup.Flow.Version, f.Role.Id);
        var start = await mcp.Call<AgentRunDetails>("neo_run_start", new { itemId = item.Id, request });
        Assert.Equal("Queued", start.Run.Status);
        Assert.Equal(start.Run.Id, (await mcp.Call<AgentRunDetails>("neo_run_start", new { itemId = item.Id, request })).Run.Id);
        Assert.Equal(3, await SqlRunTests.Drain(f, item.Id));
        var run = await mcp.Call<AgentRunDetails>("neo_run_get", new { runId = start.Run.Id });
        Assert.Equal("Waiting", run.Run.Decision); Assert.Contains("commit-required", run.Run.DecisionReason!);
        await mcp.Call<AgentRunDetails>("neo_run_handoff", new { runId = run.Run.Id,
            request = new EvaluateAgentRunRequest(Guid.NewGuid(), run.Run.WorkItemVersion, run.Run.WorkflowVersion) });
        await SqlRunTests.Drain(f, item.Id);
        run = await mcp.Call<AgentRunDetails>("neo_run_get", new { runId = start.Run.Id });
        Assert.Equal("Waiting", run.Run.Decision); // MCP never bypasses absent evidence.
        Assert.Single(await mcp.Call<AgentRunDetails[]>("neo_run_list", new { itemId = item.Id }));
        await mcp.Call<AgentRunDetails>("neo_run_return", new { runId = run.Run.Id, request = new ReturnRunAssignmentRequest(run.Run.WorkItemVersion) });
        var returned = await mcp.Call<WorkItemDetails>("neo_work_get", new { itemId = item.Id });
        Assert.Equal("agent-a", returned.Item.OwnerAgentId); Assert.Equal("mcp-test-chat", returned.Item.OwnerChatId);
        Assert.False(returned.Item.IsTracking); Assert.Empty(returned.Evidence);
    }

    // Real stdio child + real Kestrel HTTP + issuer-signed test JWT + real SQL.
    // The child receives no database or model credentials.
    private sealed class McpProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task stderrReader;
        private readonly StringBuilder errors = new();
        private int identifier;
        public string Stderr => errors.ToString();
        private McpProcess(Process process)
        {
            this.process = process;
            stderrReader = Task.Run(async () => {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    if (errors.Length < 65536) errors.AppendLine(line);
            });
        }
        public static async Task<McpProcess> Start(ApiFixture api, HttpClient identity, WorkspaceScope scope)
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "Neo.AgentOrchestration.Mcp.dll");
            if (!File.Exists(dll))
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "Neo.AgentOrchestration.slnx"))) root = root.Parent;
                Assert.NotNull(root);
                var configuration = typeof(OperationalMcpTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
                dll = Path.Combine(root.FullName, "src", "Neo.AgentOrchestration.Mcp", "bin", configuration, "net10.0", "Neo.AgentOrchestration.Mcp.dll");
            }
            Assert.True(File.Exists(dll));
            var url = api.Factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var start = new ProcessStartInfo("dotnet") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(dll);
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "SYSTEMROOT", "TEMP", "TMP", "USERPROFILE", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment["NeoMcp__ApiBaseUrl"] = url;
            start.Environment["NeoMcp__AllowLoopbackHttp"] = "true";
            start.Environment["NeoMcp__OrganizationId"] = scope.OrganizationId.ToString("D");
            start.Environment["NeoMcp__WorkspaceId"] = scope.WorkspaceId.ToString("D");
            start.Environment["NeoMcp__ChatId"] = "mcp-test-chat";
            start.Environment["NEO_ORCHESTRATION_ACCESS_TOKEN"] = identity.DefaultRequestHeaders.Authorization!.Parameter!;
            var child = new McpProcess(Process.Start(start)!);
            try {
                await child.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                    clientInfo = new { name = "neo-operational-mcp-test", version = "1.0" } });
                await child.Write(new { jsonrpc = "2.0", method = "notifications/initialized" });
                return child;
            }
            catch { await child.DisposeAsync(); throw; }
        }
        private Task Write(object value) => process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, Json));
        public async Task<JsonElement> Request(string method, object parameters)
        {
            var id = ++identifier;
            await Write(new { jsonrpc = "2.0", id, method, @params = parameters });
            await process.StandardInput.FlushAsync(Ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            while (true) {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    await stderrReader;
                    var credential = process.StartInfo.Environment["NEO_ORCHESTRATION_ACCESS_TOKEN"]!;
                    Assert.Fail("MCP exited before its response: " + Stderr.Replace(credential, "[redacted]", StringComparison.Ordinal));
                }
                // Non-JSON stdout also fails: stdio must remain protocol-only.
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var actual) || actual != id) continue;
                Assert.False(message.TryGetProperty("error", out _), "MCP protocol error.");
                return message.GetProperty("result").Clone();
            }
        }
        public async Task<T> Call<T>(string name, object? arguments = null)
        {
            var result = await Request("tools/call", new { name, arguments = arguments ?? new { } });
            Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
            return JsonSerializer.Deserialize<T>(result.GetProperty("content")[0].GetProperty("text").GetString()!, Json)!;
        }
        public async Task Error(string name, object arguments, string? contains = null)
        {
            var result = await Request("tools/call", new { name, arguments });
            Assert.True(result.TryGetProperty("isError", out var error) && error.GetBoolean());
            if (contains is not null) Assert.Contains(contains, result.GetRawText());
        }
        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await stderrReader; process.Dispose();
        }
    }
}
