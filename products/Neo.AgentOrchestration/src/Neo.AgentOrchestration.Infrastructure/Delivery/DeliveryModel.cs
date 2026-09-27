using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.Domain.Entities.Common;

namespace Neo.AgentOrchestration.Infrastructure.Delivery;

internal static class DeliveryModel
{
    public static void Configure(ModelBuilder b)
    {
        var outbox = b.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages", "nao");
        // Product actors are agent/chat identities, not Neo's legacy user IDs.
        outbox.Ignore(x => x.CreatedById).Ignore(x => x.LastModifiedById);
        outbox.HasIndex(x => new { x.OutboxState, x.NextAttemptAtUtc, x.DeliveryLeaseUntilUtc });
        outbox.HasIndex(x => new { x.TenantKey, x.IdempotencyKey }).IsUnique()
            .HasFilter("[TenantKey] IS NOT NULL AND [IdempotencyKey] IS NOT NULL");
        var delivery = b.Entity<DeliveryRecord>();
        delivery.ToTable("Deliveries", "nao");
        delivery.Property(x => x.Id).ValueGeneratedNever();
        delivery.Property(x => x.Source).HasMaxLength(80).UseCollation("Latin1_General_100_BIN2");
        delivery.Property(x => x.MessageId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        delivery.Property(x => x.PayloadHash).HasMaxLength(64).IsUnicode(false);
        delivery.HasIndex(x => new { x.WorkspaceId, x.Source, x.MessageId }).IsUnique();
        delivery.HasAlternateKey(x => new { x.Id, x.WorkItemId, x.ProjectId });
        delivery.HasOne(x => x.Outbox).WithOne().HasForeignKey<DeliveryRecord>(x => x.OutboxId).OnDelete(DeleteBehavior.Restrict);
        delivery.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        delivery.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        var inbox = b.Entity<InboxReceipt>();
        inbox.ToTable("InboxReceipts", "nao");
        inbox.Property(x => x.Id).ValueGeneratedNever();
        inbox.Property(x => x.Source).HasMaxLength(80).UseCollation("Latin1_General_100_BIN2");
        inbox.Property(x => x.MessageId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        inbox.Property(x => x.PayloadHash).HasMaxLength(64).IsUnicode(false);
        inbox.HasIndex(x => new { x.WorkspaceId, x.Source, x.MessageId }).IsUnique();
        inbox.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        inbox.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        inbox.HasOne(x => x.FollowUp).WithMany().HasForeignKey(x => new { x.FollowUpId, x.WorkItemId, x.ProjectId })
            .HasPrincipalKey(x => new { x.Id, x.WorkItemId, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
    }
}
