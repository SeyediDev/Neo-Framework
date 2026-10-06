using System.Globalization;
using System.Text.Json;

namespace Fanasa.AccessManagement.Web.Organization;

public sealed record OrgFieldDifference(string Field, string? Before, string? After);
public sealed record OrgEntityDifference(string Entity, Guid Id, string Name, string Change, OrgFieldDifference[] Fields);
public sealed record OrgComparison(long From, long To, int Total, int? NextOffset, OrgEntityDifference[] Changes);

public static class OrganizationComparison
{
    public static OrgComparison Compare(OrgState before, OrgState after, int offset = 0)
    {
        if (before.TenantId != after.TenantId || before.Revision > after.Revision || offset < 0) throw new ArgumentException("Invalid comparison range.");
        var changes = new List<OrgEntityDifference>();
        Diff("unit", before.Units, after.Units, x => x.Id, x => x.Name);
        Diff("role", before.Roles ?? [], after.Roles ?? [], x => x.Id, x => x.Name);
        Diff("position", before.Positions, after.Positions, x => x.Id, x => x.Name);
        Diff("appointment", before.Appointments, after.Appointments, x => x.Id, x => x.Subject);
        var ordered = changes.OrderBy(x => x.Entity, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
        var page = ordered.Skip(offset).Take(100).ToArray();
        return new(before.Revision, after.Revision, ordered.Length, (long)offset + page.Length < ordered.Length ? offset + page.Length : null, page);

        void Diff<T>(string entity, T[] previous, T[] next, Func<T, Guid> id, Func<T, string> name) where T : notnull
        {
            var old = previous.ToDictionary(id); var current = next.ToDictionary(id);
            foreach (var key in old.Keys.Union(current.Keys))
            {
                var existsBefore = old.TryGetValue(key, out var left); var existsAfter = current.TryGetValue(key, out var right);
                if (existsBefore && existsAfter && EqualityComparer<T>.Default.Equals(left!, right!)) continue;
                var a = existsBefore ? JsonSerializer.SerializeToElement(left) : default;
                var b = existsAfter ? JsonSerializer.SerializeToElement(right) : default;
                var fields = (existsBefore ? a.EnumerateObject().Select(x => x.Name) : []).Union(existsAfter ? b.EnumerateObject().Select(x => x.Name) : [])
                    .Where(x => x != "Id").Select(field => new OrgFieldDifference(field, Value(a, field), Value(b, field))).Where(x => x.Before != x.After).ToArray();
                changes.Add(new(entity, key, name(existsAfter ? right! : left!), existsBefore ? existsAfter ? "changed" : "removed" : "added", fields));
            }
        }
    }
    private static string? Value(JsonElement item, string field)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
}
