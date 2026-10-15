using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Domain.Advisory;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Infrastructure.Delivery;

namespace Neo.AgentOrchestration.Infrastructure.Advisory;

internal static class AdvisoryModel
{
    public static void Configure(ModelBuilder b)
    {
        var a = b.Entity<AdvisoryRequest>();
        a.ToTable("AdvisoryRequests", "nao", t => {
            t.HasCheckConstraint("CK_AdvisoryRequests_State", "[State] BETWEEN 1 AND 7");
            t.HasCheckConstraint("CK_AdvisoryRequests_Usage", "([InputTokens] IS NULL OR [InputTokens]>=0) AND ([OutputTokens] IS NULL OR [OutputTokens]>=0)");
        });
        a.HasKey(x => x.Id); a.Property(x => x.Id).ValueGeneratedNever();
        a.Property(x => x.Version).IsConcurrencyToken(); a.Property<byte[]>("RowVersion").IsRowVersion();
        a.Property(x => x.RequesterId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        a.Property(x => x.RequesterChatId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        a.Property(x => x.Fingerprint).HasMaxLength(64).IsUnicode(false);
        a.Property(x => x.Brief).HasMaxLength(2000); a.Property(x => x.Draft).HasMaxLength(12000);
        a.Property(x => x.Reason).HasMaxLength(80); a.Property(x => x.Model).HasMaxLength(200);
        a.Property(x => x.NativeRunId).HasMaxLength(200);
        a.HasIndex(x => new { x.WorkspaceId, x.RequesterId, x.RequestId }).IsUnique();
        a.HasIndex(x => x.DeliveryOperationId).IsUnique();
        a.HasIndex(x => new { x.WorkspaceId, x.WorkItemId, x.RequesterId, x.CreatedAtUtc });
        a.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        a.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        a.HasOne<DeliveryRecord>().WithMany().HasForeignKey(x => new { x.DeliveryOperationId, x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.WorkItemId, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
    }
}
