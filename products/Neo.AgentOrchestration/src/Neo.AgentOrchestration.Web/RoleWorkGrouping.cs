using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web;

public sealed record RoleWorkGroup(Guid? RoleId, string Name, IReadOnlyList<WorkItemView> Items);

public static class RoleWorkGrouping
{
    public static IReadOnlyList<RoleWorkGroup> Create(IReadOnlyList<WorkItemView> items,
        IReadOnlyList<RoleProfileView> roles, Guid? filter)
    {
        var groups = roles.Where(x => !filter.HasValue || x.Id == filter)
            .Select(role => new RoleWorkGroup(role.Id, role.Name,
                items.Where(item => Resolve(item, roles) == role.Id).ToArray())).ToList();
        if (!filter.HasValue)
        {
            foreach (var group in items.Where(item => Resolve(item, roles) is null)
                .GroupBy(item => item.OwnerRoleId.HasValue ? "رول جاری نامشخص" :
                    !string.IsNullOrWhiteSpace(item.LastOwnerHistory?.OwnerRole)
                        ? "رول تاریخی: " + item.LastOwnerHistory.OwnerRole : "بدون رول"))
                groups.Add(new(null, group.Key, group.ToArray()));
        }
        return groups;
    }

    private static Guid? Resolve(WorkItemView item, IReadOnlyList<RoleProfileView> roles)
    {
        // Never move a currently assigned item back to its historical owner.
        if (item.OwnerRoleId.HasValue)
            return roles.Any(x => x.Id == item.OwnerRoleId) ? item.OwnerRoleId : null;
        var key = item.LastOwnerHistory?.OwnerRole?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        // Only stable role keys/IDs, not ambiguous display names, are matched.
        return roles.SingleOrDefault(role => string.Equals(role.Key, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role.Id.ToString("D"), key, StringComparison.OrdinalIgnoreCase))?.Id;
    }
}
