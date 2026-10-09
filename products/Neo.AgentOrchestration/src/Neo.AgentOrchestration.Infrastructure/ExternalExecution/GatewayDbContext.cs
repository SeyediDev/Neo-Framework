using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Neo.Infrastructure.Data.Repository.Ef;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

// Dedicated execution-journal context. No WorkItems, ownership, credentials or
// identity tables. Provisioning is explicit; host startup never creates schema.
public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : EfDbContext<GatewayDbContext>(options)
{
    protected override Assembly ContextAssembly => typeof(GatewayDbContext).Assembly;
    public DbSet<GatewayRun> Runs => Set<GatewayRun>();
    public DbSet<GatewayUsageEntry> Usage => Set<GatewayUsageEntry>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        var run = b.Entity<GatewayRun>();
        run.ToTable("Runs", "gateway"); run.HasKey(x => x.RunId);
        run.Property(x => x.RunId).ValueGeneratedNever();
        run.HasAlternateKey(x => new { x.RunId, x.OrganizationId, x.WorkspaceId, x.ProjectId });
        run.Property(x => x.Version).IsConcurrencyToken();
        run.Property(x => x.BindingKey).HasMaxLength(40);
        run.Property(x => x.BindingFingerprint).HasMaxLength(64).IsUnicode(false);
        run.Property(x => x.PayloadHash).HasMaxLength(64).IsUnicode(false);
        run.Property(x => x.SandboxId).HasMaxLength(100);
        run.Property(x => x.PreparedHandle).HasMaxLength(4096);
        run.Property(x => x.ApprovalId).HasMaxLength(100);
        run.Property(x => x.ReasonCode).HasMaxLength(100);
        run.HasIndex(x => new { x.CapacityHeld, x.LeaseUntilUtc, x.Phase });
        var usage = b.Entity<GatewayUsageEntry>();
        usage.ToTable("Usage", "gateway");
        usage.HasKey(x => new { x.RunId, x.ReportId });
        usage.Property(x => x.ReportId).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        usage.Property(x => x.BodyHash).HasMaxLength(64).IsUnicode(false);
        usage.HasOne<GatewayRun>().WithMany().HasForeignKey(x => new { x.RunId, x.OrganizationId, x.WorkspaceId, x.ProjectId })
            .HasPrincipalKey(x => new { x.RunId, x.OrganizationId, x.WorkspaceId, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GatewayUsageEntry
{
    public Guid RunId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public string ReportId { get; set; } = "";
    public string BodyHash { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
}

public sealed class GatewayDesignFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<GatewayDbContext>()
        .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FanasaAgentGatewayDesign;Integrated Security=true;TrustServerCertificate=true")
        .Options);
}
