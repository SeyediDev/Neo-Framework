using Neo.Domain.Entities.Common;
using Microsoft.EntityFrameworkCore;

namespace Neo.Infrastructure.Data.Settings.Rdbms;

public class SettingDbContext(DbContextOptions<SettingDbContext> dbContextOptions)
    : DbContext(dbContextOptions)
{
    public DbSet<Setting> Settings { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Setting>(b =>
        {
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(100);
        });
    }
}