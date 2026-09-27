using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Data.Repository.Ef;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

public sealed class OrchestrationDbContext(DbContextOptions<OrchestrationDbContext> options)
    : EfDbContext<OrchestrationDbContext>(options)
{
    protected override Assembly ContextAssembly => typeof(OrchestrationDbContext).Assembly;
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<RoleProfile> Roles => Set<RoleProfile>();
    public DbSet<AgentProfile> Agents => Set<AgentProfile>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<WorkflowDefinition> Workflows => Set<WorkflowDefinition>();
    public DbSet<WorkflowApproval> Approvals => Set<WorkflowApproval>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<DeliveryRecord> Deliveries => Set<DeliveryRecord>();
    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        OrchestrationModel.Configure(builder);
        AgentRunModel.Configure(builder);
        DeliveryModel.Configure(builder);
    }
}

// Schema tooling never reads Hyper configuration or runs a migration.
public sealed class OrchestrationDesignFactory : IDesignTimeDbContextFactory<OrchestrationDbContext>
{
    public OrchestrationDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OrchestrationDbContext>()
        .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=NeoAgentOrchestrationDesign;Integrated Security=true;TrustServerCertificate=true")
        .Options);
}
