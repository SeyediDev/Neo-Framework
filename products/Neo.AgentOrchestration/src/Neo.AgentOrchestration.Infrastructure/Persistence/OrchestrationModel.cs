using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

internal static class OrchestrationModel
{
    public static void Configure(ModelBuilder b)
    {
        var org = Entity<Organization>(b, "Organizations");
        org.Property(x => x.Key).HasMaxLength(80); org.Property(x => x.Name).HasMaxLength(200);
        org.HasIndex(x => x.Key).IsUnique();
        var workspace = Entity<Workspace>(b, "Workspaces");
        workspace.Ignore(x => x.Scope);
        workspace.Property(x => x.Key).HasMaxLength(80); workspace.Property(x => x.Name).HasMaxLength(200);
        workspace.HasAlternateKey(x => new { x.Id, x.OrganizationId });
        workspace.HasIndex(x => new { x.OrganizationId, x.Key }).IsUnique();
        workspace.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        var project = Entity<Project>(b, "Projects");
        project.Property(x => x.Key).HasMaxLength(80); project.Property(x => x.Name).HasMaxLength(200);
        project.HasAlternateKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId });
        project.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique();
        project.HasOne<Workspace>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        var binding = Entity<ProjectRepositoryBinding>(b, "ProjectRepositoryBindings");
        binding.Property(x => x.Provider).HasMaxLength(80);
        binding.Property(x => x.RepositoryUrl).HasMaxLength(2000);
        binding.Property(x => x.RepositoryKey).HasMaxLength(200);
        binding.Property(x => x.DefaultBranch).HasMaxLength(250);
        binding.Property(x => x.DevelopmentBranch).HasMaxLength(250);
        binding.Property(x => x.CiCdReference).HasMaxLength(500);
        binding.Property(x => x.SecretReference).HasMaxLength(500);
        binding.HasIndex(x => x.ProjectId).IsUnique();
        binding.HasAlternateKey(x => new { x.Id, x.ProjectId });
        binding.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        var role = Entity<RoleProfile>(b, "RoleProfiles");
        role.Property(x => x.Key).HasMaxLength(80); role.Property(x => x.Name).HasMaxLength(200);
        role.Property(x => x.ScopeDescription).HasMaxLength(4000);
        role.Property(x => x.MaxConcurrentWorkItems).HasDefaultValue(1);
        role.HasAlternateKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId });
        role.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique();
        role.HasOne<Workspace>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        var agent = Entity<AgentProfile>(b, "AgentProfiles");
        agent.Property(x => x.Key).HasMaxLength(80); agent.Property(x => x.Name).HasMaxLength(200);
        agent.Property(x => x.Provider).HasMaxLength(80); agent.Property(x => x.Model).HasMaxLength(200);
        agent.Property(x => x.Instructions).HasMaxLength(32000); agent.Property(x => x.SkillPath).HasMaxLength(512);
        agent.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique();
        agent.HasOne<RoleProfile>().WithMany().HasForeignKey(x => new { x.RoleProfileId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        ConfigureWork(b);
        ConfigureWorkflows(b);
    }

    private static void ConfigureWork(ModelBuilder b)
    {
        var work = Entity<WorkItem>(b, "WorkItems");
        work.Ignore(x => x.IsTracking);
        work.Property(x => x.Version).IsConcurrencyToken();
        work.Property(x => x.Key).HasMaxLength(80); work.Property(x => x.Title).HasMaxLength(200);
        work.Property(x => x.Domain).HasMaxLength(80); work.Property(x => x.Description).HasMaxLength(32000);
        work.Property(x => x.Type).HasDefaultValue(WorkItemType.Task);
        work.Property(x => x.AcceptanceCriteria).HasMaxLength(8000);
        work.Property(x => x.OwnerAgentId).HasMaxLength(200); work.Property(x => x.OwnerChatId).HasMaxLength(200);
        work.Property(x => x.Branch).HasMaxLength(250);
        work.HasAlternateKey(x => new { x.Id, x.ProjectId });
        work.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
        work.HasIndex(x => new { x.WorkspaceId, x.ProjectId, x.Domain, x.Status });
        work.HasIndex(x => x.OwnerRoleId).HasFilter("[Status] = 3 AND [OwnerRoleId] IS NOT NULL");
        work.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        work.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.ParentWorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        work.HasOne<RoleProfile>().WithMany().HasForeignKey(x => new { x.OwnerRoleId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        work.ToTable("WorkItems", "nao", t =>
        {
            t.HasCheckConstraint("CK_WorkItems_Status", "[Status] BETWEEN 1 AND 7 AND [Priority] BETWEEN 1 AND 4");
            t.HasCheckConstraint("CK_WorkItems_Type", "[Type] BETWEEN 1 AND 4");
            t.HasCheckConstraint("CK_WorkItems_Estimate", "[EstimatedSeconds] IS NULL OR [EstimatedSeconds] > 0");
            t.HasCheckConstraint("CK_WorkItems_TokenEstimate", "[EstimatedTokens] IS NULL OR [EstimatedTokens] > 0");
            t.HasCheckConstraint("CK_WorkItems_Owner", "([OwnerRoleId] IS NULL AND [OwnerAgentId] IS NULL AND [OwnerChatId] IS NULL AND [Status] <> 3) OR ([OwnerRoleId] IS NOT NULL AND [OwnerAgentId] IS NOT NULL AND [OwnerChatId] IS NOT NULL)");
            t.HasCheckConstraint("CK_WorkItems_Parent", "[ParentWorkItemId] IS NULL OR [ParentWorkItemId] <> [Id]");
        });
        var log = Entity<WorkItemLog>(b, "WorkItemLogs");
        log.Property(x => x.AgentId).HasMaxLength(200); log.Property(x => x.ChatId).HasMaxLength(200);
        log.Property(x => x.Kind).HasMaxLength(80); log.Property(x => x.Message).HasMaxLength(8000);
        log.HasIndex(x => new { x.WorkItemId, x.CreatedAtUtc });
        work.HasMany(x => x.Logs).WithOne().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
        work.Navigation(x => x.Logs).HasField("_logs").UsePropertyAccessMode(PropertyAccessMode.Field);
        var evidence = Entity<WorkItemEvidence>(b, "WorkItemEvidence");
        evidence.Property(x => x.Reference).HasMaxLength(2000); evidence.Property(x => x.Details).HasMaxLength(8000);
        evidence.Property(x => x.CommitSha).HasMaxLength(64);
        evidence.HasIndex(x => new { x.WorkItemId, x.Sequence }).IsUnique();
        work.HasMany(x => x.Evidence).WithOne().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
        work.Navigation(x => x.Evidence).HasField("_evidence").UsePropertyAccessMode(PropertyAccessMode.Field);
        var time = Entity<WorkItemTimeEntry>(b, "WorkItemTimeEntries");
        time.HasIndex(x => x.WorkItemId).IsUnique().HasFilter("[EndedAtUtc] IS NULL");
        time.ToTable("WorkItemTimeEntries", "nao", t => t.HasCheckConstraint("CK_Time_Duration",
            "[DurationSeconds] >= 0 AND ([EndedAtUtc] IS NULL OR [EndedAtUtc] >= [StartedAtUtc])"));
        work.HasMany(x => x.TimeEntries).WithOne().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
        work.Navigation(x => x.TimeEntries).HasField("_timeEntries").UsePropertyAccessMode(PropertyAccessMode.Field);
        var usage = Entity<WorkTokenUsage>(b, "WorkTokenUsage");
        usage.Property(x => x.AgentId).HasMaxLength(200); usage.Property(x => x.ChatId).HasMaxLength(200);
        usage.Property(x => x.Provider).HasMaxLength(80); usage.Property(x => x.Model).HasMaxLength(200);
        usage.Property(x => x.Reference).HasMaxLength(500);
        usage.HasIndex(x => new { x.WorkItemId, x.RecordedAtUtc });
        usage.ToTable("WorkTokenUsage", "nao", t => t.HasCheckConstraint("CK_WorkTokenUsage_Counts",
            "([InputTokens] IS NULL OR [InputTokens] >= 0) AND ([OutputTokens] IS NULL OR [OutputTokens] >= 0) AND ([CachedInputTokens] IS NULL OR [CachedInputTokens] >= 0) AND ([ReasoningTokens] IS NULL OR [ReasoningTokens] >= 0) AND ([InputTokens] IS NULL OR [CachedInputTokens] IS NULL OR [CachedInputTokens] <= [InputTokens]) AND ([OutputTokens] IS NULL OR [ReasoningTokens] IS NULL OR [ReasoningTokens] <= [OutputTokens]) AND ([InputTokens] IS NOT NULL OR [OutputTokens] IS NOT NULL OR [CachedInputTokens] IS NOT NULL OR [ReasoningTokens] IS NOT NULL)"));
        work.HasMany(x => x.TokenUsage).WithOne().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
        work.Navigation(x => x.TokenUsage).HasField("_tokenUsage").UsePropertyAccessMode(PropertyAccessMode.Field);
        var dependency = Entity<WorkItemDependency>(b, "WorkItemDependencies");
        dependency.HasIndex(x => new { x.WorkItemId, x.DependsOnWorkItemId }).IsUnique();
        dependency.ToTable("WorkItemDependencies", "nao", t => t.HasCheckConstraint("CK_Dependency_Self", "[WorkItemId] <> [DependsOnWorkItemId]"));
        work.HasMany(x => x.Dependencies).WithOne().HasForeignKey(x => new { x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        dependency.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.DependsOnWorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        work.Navigation(x => x.Dependencies).HasField("_dependencies").UsePropertyAccessMode(PropertyAccessMode.Field);
        var ownerHistory = Entity<WorkItemOwnerHistory>(b, "WorkItemOwnerHistory");
        ownerHistory.Property(x => x.OwnerRole).HasMaxLength(200);
        ownerHistory.Property(x => x.OwnerAgent).HasMaxLength(200);
        ownerHistory.Property(x => x.OwnerChat).HasMaxLength(200);
        ownerHistory.HasIndex(x => new { x.ProjectId, x.WorkItemId, x.CreatedAtUtc });
        ownerHistory.HasOne<WorkItem>().WithMany(x => x.OwnerHistory)
            .HasForeignKey(x => new { x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        work.Navigation(x => x.OwnerHistory).HasField("_ownerHistory").UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureWorkflows(ModelBuilder b)
    {
        var flow = Entity<WorkflowDefinition>(b, "WorkflowDefinitions");
        flow.Property(x => x.Key).HasMaxLength(80); flow.Property(x => x.Name).HasMaxLength(200);
        flow.Property(x => x.Version).IsConcurrencyToken();
        flow.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
        flow.HasAlternateKey(x => new { x.Id, x.ProjectId });
        flow.HasAlternateKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId });
        flow.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        var transition = Entity<WorkflowTransition>(b, "WorkflowTransitions");
        transition.Property(x => x.Key).HasMaxLength(80); transition.Property(x => x.RequiredArtifact).HasMaxLength(2000);
        transition.HasAlternateKey(x => new { x.Id, x.WorkflowDefinitionId });
        transition.HasIndex(x => new { x.WorkflowDefinitionId, x.Key }).IsUnique();
        transition.HasIndex(x => new { x.WorkflowDefinitionId, x.FromRoleId, x.FromStatus }).IsUnique().HasFilter("[IsEnabled] = 1");
        flow.HasMany(x => x.Transitions).WithOne().HasForeignKey(x => new { x.WorkflowDefinitionId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        flow.Navigation(x => x.Transitions).HasField("_transitions").UsePropertyAccessMode(PropertyAccessMode.Field);
        transition.HasOne<RoleProfile>().WithMany().HasForeignKey(x => new { x.FromRoleId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        transition.HasOne<RoleProfile>().WithMany().HasForeignKey(x => new { x.ToRoleId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        var approval = Entity<WorkflowApproval>(b, "WorkflowApprovals");
        approval.Property(x => x.ReviewerAgentId).HasMaxLength(200); approval.Property(x => x.ReviewerChatId).HasMaxLength(200);
        approval.Property(x => x.Reason).HasMaxLength(8000);
        approval.HasIndex(x => new { x.WorkItemId, x.WorkItemVersion, x.WorkflowVersion });
        approval.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        approval.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => new { x.WorkflowDefinitionId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        approval.HasOne<WorkflowTransition>().WithMany().HasForeignKey(x => new { x.TransitionId, x.WorkflowDefinitionId })
            .HasPrincipalKey(x => new { x.Id, x.WorkflowDefinitionId }).OnDelete(DeleteBehavior.Restrict);
    }

    private static EntityTypeBuilder<T> Entity<T>(ModelBuilder b, string table) where T : BaseEntity<Guid>
    {
        var entity = b.Entity<T>();
        entity.ToTable(table, "nao");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property<byte[]>("RowVersion").IsRowVersion();
        return entity;
    }
}
