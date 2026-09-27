using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

// Application fixture only. SQL transactions, locks, rollback and durable
// concurrency are deliberately not simulated or claimed by these tests.
internal sealed class WorkFixture : IDisposable
{
    private readonly ServiceProvider services;
    public Workspace Workspace { get; } = Workspace.Create(Organization.Create("org", "Org"), "work", "Workspace");
    public Project Project { get; }
    public RoleProfile Role { get; }
    public WorkActor Actor { get; } = new("agent-a", "chat-a");
    public ManualClock Clock { get; } = new();
    public MemoryWorkStore Store { get; }
    public ISender Sender => services.GetRequiredService<ISender>();
    public WorkspaceScope Scope => Workspace.Scope;
    public CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorkFixture()
    {
        Project = Project.Create(Workspace, "project", "Project");
        Role = RoleProfile.Create(Workspace, "developer", "Developer");
        Store = new MemoryWorkStore(Workspace, [Project], [Role]);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IWorkspaceWorkStore>(Store);
        collection.AddSingleton<TimeProvider>(Clock);
        collection.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<WorkItemHandlers>());
        services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public Task<WorkItemDetails> Create(string key, Guid? parent = null, long? estimate = null)
        => Sender.Send(new CreateWorkItem(Scope, Project.Id, key, key, "integration", Actor,
            Description: "Task context remains available.", ParentWorkItemId: parent, EstimatedSeconds: estimate), Ct);

    public Task<WorkItemDetails> Change(Guid id, WorkItemChange change, WorkActor? actor = null, Guid? version = null)
        => Sender.Send(new UpdateWorkItem(Scope, id, actor ?? Actor, version ?? Store.Items.Single(x => x.Id == id).Version, change), Ct);

    public Task<WorkItemDetails> Claim(Guid id, WorkActor? actor = null)
        => Sender.Send(new ClaimWorkItem(Scope, id, Role.Id, actor ?? Actor,
            Store.Items.Single(x => x.Id == id).Version, "develop"), Ct);

    public async Task Complete(Guid id)
    {
        await Change(id, new StatusChange(WorkItemStatus.Ready));
        await Claim(id);
        await Change(id, new StatusChange(WorkItemStatus.Review));
        await Change(id, new StatusChange(WorkItemStatus.Done));
    }

    public void Dispose() => services.Dispose();
}

internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
}

internal sealed class MemoryWorkStore(Workspace workspace, List<Project> projects, List<RoleProfile> roles) : IWorkspaceWorkStore
{
    public Workspace Workspace { get; } = workspace;
    public List<AgentProfile> Agents { get; } = [];
    public List<WorkflowDefinition> Workflows { get; } = [];
    public List<WorkflowApproval> Approvals { get; } = [];
    public List<Project> Projects { get; } = projects;
    public List<RoleProfile> Roles { get; } = roles;
    public List<WorkItem> Items { get; } = [];

    public Task<T> ExecuteAsync<T>(WorkspaceScope scope, Func<IWorkItemSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return operation(new Session(this, scope), ct);
    }

    private sealed class Session(MemoryWorkStore store, WorkspaceScope scope) : IWorkItemSession
    {
        public Task<Workspace> GetWorkspaceAsync(CancellationToken ct) => Task.FromResult(store.Workspace);
        public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Project>>(
            store.Projects.Where(x => Matches(x.OrganizationId, x.WorkspaceId)).ToArray());
        public Task<IReadOnlyList<RoleProfile>> GetRolesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RoleProfile>>(
            store.Roles.Where(x => Matches(x.OrganizationId, x.WorkspaceId)).ToArray());
        public Task<IReadOnlyList<AgentProfile>> GetAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentProfile>>(
            store.Agents.Where(x => Matches(x.OrganizationId, x.WorkspaceId)).ToArray());
        public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkflowDefinition>>(
            store.Workflows.Where(x => Matches(x.OrganizationId, x.WorkspaceId)).ToArray());
        public Task<IReadOnlyList<WorkflowApproval>> GetApprovalsAsync(Guid workflowId, Guid workItemId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkflowApproval>>(store.Approvals.Where(x => x.WorkflowDefinitionId == workflowId &&
                x.WorkItemId == workItemId && store.Workflows.Any(w => w.Id == workflowId && Matches(w.OrganizationId, w.WorkspaceId))).ToArray());
        public void Add(Project value) { value.RequireScope(scope); store.Projects.Add(value); }
        public void Add(RoleProfile value) { value.RequireScope(scope); store.Roles.Add(value); }
        public void Add(AgentProfile value) { value.RequireScope(scope); store.Agents.Add(value); }
        public void Add(WorkflowDefinition value) { value.RequireScope(scope); store.Workflows.Add(value); }
        public void Add(WorkflowApproval value) => store.Approvals.Add(value);
        private bool Matches(Guid org, Guid workspace) => org == scope.OrganizationId && workspace == scope.WorkspaceId;
        public Task<Project?> GetProjectAsync(Guid id, CancellationToken ct) => Task.FromResult(
            store.Projects.SingleOrDefault(x => x.Id == id && Matches(x.OrganizationId, x.WorkspaceId)));
        public Task<RoleProfile?> GetRoleAsync(Guid id, CancellationToken ct) => Task.FromResult(
            store.Roles.SingleOrDefault(x => x.Id == id && Matches(x.OrganizationId, x.WorkspaceId)));
        public Task<WorkItem?> GetItemAsync(Guid id, CancellationToken ct) => Task.FromResult(
            store.Items.SingleOrDefault(x => x.Id == id && Matches(x.OrganizationId, x.WorkspaceId)));
        public Task<IReadOnlyList<WorkItem>> GetProjectItemsAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkItem>>(store.Items.Where(x => x.ProjectId == projectId &&
                Matches(x.OrganizationId, x.WorkspaceId)).ToArray());
        public Task<bool> IsRoleBusyAsync(Guid roleId, Guid exceptItemId, CancellationToken ct)
            => Task.FromResult(store.Items.Any(x => x.Id != exceptItemId && x.OwnerRoleId == roleId &&
                x.Status == WorkItemStatus.InProgress && Matches(x.OrganizationId, x.WorkspaceId)));
        public void Add(WorkItem item) { scope.Require(item.OrganizationId, item.WorkspaceId); store.Items.Add(item); }
    }
}
