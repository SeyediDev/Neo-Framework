using Neo.FanasaIdentity.Contracts;

namespace Neo.FanasaIdentity.Domain;

public sealed class HumanIdentity
{
    private readonly HashSet<string> _tenantIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _roles = new(StringComparer.OrdinalIgnoreCase);

    private HumanIdentity(string subject, string provider, DateTimeOffset registeredAt)
    {
        Subject = subject;
        Provider = provider;
        RegisteredAt = registeredAt;
    }

    public string Subject { get; }
    public string Provider { get; }
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    public DateTimeOffset RegisteredAt { get; }
    public IReadOnlyCollection<string> TenantIds => _tenantIds;
    public IReadOnlyCollection<string> Roles => _roles;

    public static HumanIdentity Register(string subject, string provider, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("Subject is required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider is required.", nameof(provider));
        return new HumanIdentity(subject.Trim(), provider.Trim(), now);
    }

    public void SynchronizeProfile(string? email, string? displayName, IEnumerable<string> tenantIds, IEnumerable<string> roles)
    {
        Email = string.IsNullOrWhiteSpace(email) ? Email : email.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? DisplayName : displayName.Trim();
        _tenantIds.UnionWith(tenantIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        _roles.UnionWith(roles.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
    }

    public IdentityRegistrationResponse ToResponse() => new(Subject, Provider, _tenantIds.Order().ToArray(), _roles.Order().ToArray(), RegisteredAt);
}
