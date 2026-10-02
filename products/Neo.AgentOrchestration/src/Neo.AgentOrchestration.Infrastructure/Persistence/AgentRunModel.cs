using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

internal static class AgentRunModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<AgentProfile>().HasAlternateKey(x => new { x.Id, x.RoleProfileId, x.WorkspaceId, x.OrganizationId });
        var run = b.Entity<AgentRun>();
        run.ToTable("AgentRuns", "nao", t =>
        {
            t.HasCheckConstraint("CK_AgentRuns_Status", "[Status] BETWEEN 1 AND 5 AND [Decision] BETWEEN 0 AND 4 AND [SimulationOutcome] BETWEEN 1 AND 3");
            t.HasCheckConstraint("CK_AgentRuns_Hop", "[Hop] BETWEEN 1 AND 20 AND ([PreviousRunId] IS NULL OR [PreviousRunId] <> [Id])");
        });
        run.HasKey(x => x.Id); run.Property(x => x.Id).ValueGeneratedNever();
        run.Property<byte[]>("RowVersion").IsRowVersion(); run.Ignore(x => x.Actor);
        run.HasAlternateKey(x => new { x.Id, x.WorkItemId, x.ProjectId });
        run.HasAlternateKey(x => new { x.Id, x.WorkItemId });
        run.HasIndex(x => x.WorkItemId).IsUnique().HasFilter("[Status] IN (1, 2)");
        run.HasIndex(x => x.PreviousRunId).IsUnique().HasFilter("[PreviousRunId] IS NOT NULL");
        run.Property(x => x.RequestedByAgentId).HasMaxLength(200); run.Property(x => x.RequestedByChatId).HasMaxLength(200);
        run.Property(x => x.Provider).HasMaxLength(80); run.Property(x => x.Model).HasMaxLength(200);
        run.Property(x => x.Instructions).HasMaxLength(32000); run.Property(x => x.SkillPath).HasMaxLength(512);
        run.Property(x => x.Branch).HasMaxLength(250); run.Property(x => x.DecisionReason).HasMaxLength(500);
        run.Property(x => x.ResultSummary).HasMaxLength(8000);
        run.Property(x => x.HarnessBinding).HasMaxLength(64).IsUnicode(false);
        run.Property(x => x.HarnessPayload).HasColumnType("nvarchar(max)");
        run.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        run.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        run.HasOne<WorkflowDefinition>().WithMany().HasForeignKey(x => new { x.WorkflowId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        run.HasOne<AgentProfile>().WithMany().HasForeignKey(x => new { x.AgentProfileId, x.RoleId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.RoleProfileId, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        run.HasOne<AgentRun>().WithMany().HasForeignKey(x => new { x.PreviousRunId, x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.WorkItemId, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        var usage = b.Entity<TokenUsageReport>();
        usage.ToTable("TokenUsageReports", "nao", t =>
            t.HasCheckConstraint("CK_TokenUsageReports_Counts", "([InputTokens] IS NULL OR [InputTokens] >= 0) AND ([OutputTokens] IS NULL OR [OutputTokens] >= 0) AND ([CachedInputTokens] IS NULL OR [CachedInputTokens] >= 0) AND ([ReasoningTokens] IS NULL OR [ReasoningTokens] >= 0)"));
        usage.HasKey(x => x.Id); usage.Property(x => x.Id).ValueGeneratedNever();
        usage.HasAlternateKey(x => new { x.Id, x.WorkItemId, x.AgentRunId });
        usage.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.AgentRunId, x.RecordedAtUtc });
        usage.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.IdempotencyKey }).IsUnique();
        usage.Property(x => x.Provider).HasMaxLength(80); usage.Property(x => x.Model).HasMaxLength(200);
        usage.Property(x => x.Source).HasMaxLength(40); usage.Property(x => x.IdempotencyKey).HasMaxLength(120);
        usage.HasOne<AgentRun>().WithMany().HasForeignKey(x => new { x.AgentRunId, x.WorkItemId })
            .HasPrincipalKey(x => new { x.Id, x.WorkItemId }).OnDelete(DeleteBehavior.Restrict);
    }
}
