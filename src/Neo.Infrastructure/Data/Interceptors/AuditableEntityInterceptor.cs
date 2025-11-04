using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Client;
using DNTPersianUtils.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Neo.Infrastructure.Data.Interceptors;

public class AuditableEntityInterceptor(
    IRequesterUser user,
    TimeProvider dateTime) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        UpdateEntities(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        UpdateEntities(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public void UpdateEntities(DbContext? context)
    {
        if (context == null) return;

        foreach (var entry in context.ChangeTracker.Entries<IBaseAuditableEntity>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified || HasChangedOwnedEntities())
            {
                var utcNow = dateTime.GetLocalNow();
                if (entry.State == EntityState.Added)
                {
                    entry.Entity.CreatedById = user.Id;
                    entry.Entity.CreateDate = utcNow.LocalDateTime;
                }
                entry.Entity.LastModifiedById = user.Id;
                entry.Entity.LastModified = utcNow.LocalDateTime;
                foreach (var property in entry.Properties)
                {
                    if (property.Metadata.ClrType == typeof(string) && property.CurrentValue != null)
                    {
                        var current = property.CurrentValue as string;
                        if (!string.IsNullOrWhiteSpace(current))
                        {
                            // Convert Persian/Arabic digits to English digits
                            var cleaned = current.ApplyCorrectYeKe().ToEnglishNumbers();

                            // Assign back if it changed
                            if (cleaned != current)
                            {
                                property.CurrentValue = cleaned;
                            }
                        }
                    }
                }
            }
            bool HasChangedOwnedEntities() => entry.References.Any(r =>
                r.TargetEntry != null &&
                r.TargetEntry.Metadata.IsOwned() &&
                (r.TargetEntry.State == EntityState.Added || r.TargetEntry.State == EntityState.Modified));
        }
    }
}
