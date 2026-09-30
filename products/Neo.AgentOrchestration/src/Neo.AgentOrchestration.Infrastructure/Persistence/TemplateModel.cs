using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Domain.Templates;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

internal static class TemplateModel
{
    public static void Configure(ModelBuilder builder)
    {
        var template = builder.Entity<ProjectTemplate>();
        template.ToTable("ProjectTemplates", "nao", t => t.HasCheckConstraint("CK_Template_Revision", "[Revision] > 0"));
        template.HasKey(x => x.Id); template.Property(x => x.Id).ValueGeneratedNever();
        template.Property<byte[]>("RowVersion").IsRowVersion();
        template.Property(x => x.Key).HasMaxLength(80); template.Property(x => x.Name).HasMaxLength(200);
        template.Property(x => x.DefinitionJson).HasMaxLength(64000); template.Property(x => x.ContentHash).HasMaxLength(64);
        template.Property(x => x.CreatedByAgent).HasMaxLength(200); template.Property(x => x.CreatedByChat).HasMaxLength(200);
        template.HasAlternateKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId });
        template.HasIndex(x => new { x.WorkspaceId, x.Key, x.Revision }).IsUnique();
        template.HasOne<Workspace>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        var receipt = builder.Entity<TemplateInstantiation>();
        receipt.ToTable("TemplateInstantiations", "nao");
        receipt.HasKey(x => x.Id); receipt.Property(x => x.Id).ValueGeneratedNever();
        receipt.Property<byte[]>("RowVersion").IsRowVersion();
        receipt.Property(x => x.Fingerprint).HasMaxLength(64);
        receipt.Property(x => x.ParametersJson).HasMaxLength(40000); receipt.Property(x => x.MappingJson).HasMaxLength(40000);
        receipt.Property(x => x.CreatedByAgent).HasMaxLength(200); receipt.Property(x => x.CreatedByChat).HasMaxLength(200);
        receipt.HasIndex(x => new { x.WorkspaceId, x.RequestId }).IsUnique();
        receipt.HasIndex(x => x.ProjectId).IsUnique();
        receipt.HasOne<ProjectTemplate>().WithMany().HasForeignKey(x => new { x.TemplateId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }
}
