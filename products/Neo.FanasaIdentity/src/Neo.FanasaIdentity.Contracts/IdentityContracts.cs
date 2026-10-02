namespace Neo.FanasaIdentity.Contracts;

public static class FanasaIdentityClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Tenant = "tenant";
    public const string TenantList = "tenants";
    public const string HumanRole = "human_role";
    public const string Provider = "idp";
}

public sealed record AuthenticatedHuman(
    string Subject,
    string? Email,
    string? DisplayName,
    string Provider,
    IReadOnlyCollection<string> TenantIds,
    IReadOnlyCollection<string> Roles);

public sealed record IdentityRegistrationRequest(
    string? DisplayName,
    string? Email,
    IReadOnlyCollection<string> TenantIds,
    IReadOnlyCollection<string> Roles);

public sealed record IdentityRegistrationResponse(
    string Subject,
    string Provider,
    IReadOnlyCollection<string> TenantIds,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset RegisteredAt);
