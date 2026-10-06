using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Fanasa.AccessManagement.Web.Domain.Access;

namespace Fanasa.AccessManagement.Web.Platform;

public sealed record PlatformTenant(Guid Id, string Name, string Namespace);
public sealed record PlatformGrant(string Subject, Guid TenantId, string Permission, DateTimeOffset? ExpiresAt);
public sealed record PlatformProduct(Guid Id, string Key, string DisplayName, string Audience, string Center,
    string? Url, string? Repository, Guid? TenantId, string Owner, Dictionary<string, string>? Attributes = null);
public static class PlatformAuthorization
{
    public static bool IsAdmin(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.HasClaim("permission", "platform.admin");
}

public sealed class PlatformRegistry
{
    private Func<IReadOnlyCollection<Tenant>>? tenantSource;
    public void SetTenantSource(Func<IReadOnlyCollection<Tenant>> source) => tenantSource = source;
    private Func<Guid, IReadOnlyCollection<TenantUser>>? membershipSource;
    public void SetMembershipSource(Func<Guid, IReadOnlyCollection<TenantUser>> source) => membershipSource = source;
    private readonly string connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public PlatformRegistry(IConfiguration configuration)
    {
        var path = configuration["Platform:RegistryPath"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "platform-registry.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS platform_tenants(id TEXT PRIMARY KEY,name TEXT NOT NULL,namespace TEXT NOT NULL UNIQUE); CREATE TABLE IF NOT EXISTS platform_grants(subject TEXT NOT NULL,tenant TEXT NOT NULL,permission TEXT NOT NULL,expires TEXT,PRIMARY KEY(subject,tenant,permission)); CREATE TABLE IF NOT EXISTS platform_products(id TEXT NOT NULL UNIQUE,key TEXT PRIMARY KEY,data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS platform_audit(id INTEGER PRIMARY KEY,actor TEXT NOT NULL,operation TEXT NOT NULL,data TEXT NOT NULL,recorded TEXT NOT NULL); CREATE TABLE IF NOT EXISTS platform_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);";
        command.ExecuteNonQuery();
        var internalTenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        command.CommandText = "INSERT OR IGNORE INTO platform_tenants(id,name,namespace) VALUES($id,$name,$namespace)";
        command.Parameters.AddWithValue("$id", internalTenant.ToString()); command.Parameters.AddWithValue("$name", "سازمان فن‌آسا"); command.Parameters.AddWithValue("$namespace", "tenant-fanasa"); command.ExecuteNonQuery();
        var seed = configuration["Platform:BootstrapSubject"];
        command.Parameters.Clear(); command.CommandText = "SELECT value FROM platform_meta WHERE key='bootstrapped'";
        if (!string.IsNullOrEmpty(seed) && command.ExecuteScalar() is null)
        {
            foreach (var permission in new[] { "developer.read", "developer.launch", "developer.manage" }) Grant(new(seed, internalTenant, permission, null), "bootstrap");
            command.CommandText = "INSERT INTO platform_meta(key,value) VALUES('bootstrapped','true')"; command.ExecuteNonQuery();
        }
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public bool Allows(string subject, Guid tenant, string permission)
    {
        if (tenantSource is null || membershipSource is null
            || !tenantSource().Any(x => x.Id == tenant && x.IsActive)
            || !membershipSource(tenant).Any(x => x.KeycloakSubject == subject && x.IsActive)) return false;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT expires FROM platform_grants WHERE subject=$subject AND tenant=$tenant AND permission=$permission";
        command.Parameters.AddWithValue("$subject", subject); command.Parameters.AddWithValue("$tenant", tenant.ToString()); command.Parameters.AddWithValue("$permission", permission);
        using var reader = command.ExecuteReader(); if (!reader.Read()) return false;
        return reader.IsDBNull(0) || DateTimeOffset.Parse(reader.GetString(0)) > DateTimeOffset.UtcNow;
    }
    public PlatformTenant[] Tenants(string? subject = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT id,name,namespace FROM platform_tenants";
        using var reader = command.ExecuteReader(); var rows = new List<PlatformTenant>();
        while (reader.Read()) rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        var active = tenantSource?.Invoke().Where(x => x.IsActive).ToDictionary(x => x.Id);
        if (active is not null) rows = rows.Where(x => active.ContainsKey(x.Id)).Select(x => x with { Name = active[x.Id].DisplayName }).ToList();
        return rows.Where(row => subject is null || Allows(subject, row.Id, "developer.read")).ToArray();
    }
    public void Grant(PlatformGrant grant, string actor)
    {
        if (string.IsNullOrWhiteSpace(grant.Subject) || grant.Subject.Length > 120 || !Tenants().Any(x => x.Id == grant.TenantId)
            || grant.Permission is not ("developer.read" or "developer.launch" or "developer.manage")) throw new ArgumentException("Subject، سازمان یا مجوز معتبر نیست.");
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO platform_grants(subject,tenant,permission,expires) VALUES($subject,$tenant,$permission,$expires) ON CONFLICT(subject,tenant,permission) DO UPDATE SET expires=excluded.expires";
        command.Parameters.AddWithValue("$subject", grant.Subject); command.Parameters.AddWithValue("$tenant", grant.TenantId.ToString()); command.Parameters.AddWithValue("$permission", grant.Permission);
        command.Parameters.AddWithValue("$expires", (object?)grant.ExpiresAt?.ToString("O") ?? DBNull.Value); command.ExecuteNonQuery(); Audit(actor, "grant.save", grant);
    }
    public void Revoke(PlatformGrant grant, string actor)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM platform_grants WHERE subject=$subject AND tenant=$tenant AND permission=$permission";
        command.Parameters.AddWithValue("$subject", grant.Subject); command.Parameters.AddWithValue("$tenant", grant.TenantId.ToString()); command.Parameters.AddWithValue("$permission", grant.Permission);
        command.ExecuteNonQuery(); Audit(actor, "grant.revoke", grant);
    }
    public PlatformGrant[] Grants()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT subject,tenant,permission,expires FROM platform_grants";
        using var reader = command.ExecuteReader(); var values = new List<PlatformGrant>();
        while (reader.Read()) values.Add(new(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3))));
        return values.ToArray();
    }
    public PlatformProduct[] Products(string? subject = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT data FROM platform_products ORDER BY key";
        using var reader = command.ExecuteReader(); var values = new List<PlatformProduct>();
        while (reader.Read()) values.Add(JsonSerializer.Deserialize<PlatformProduct>(reader.GetString(0), JsonOptions)!);
        return values.Where(value => subject is null || value.TenantId.HasValue && Allows(subject, value.TenantId.Value, "developer.read")).ToArray();
    }
    public PlatformProduct Register(PlatformProduct product, string actor)
    {
        if (string.IsNullOrWhiteSpace(product.Key) || product.Key.Length > 90 || string.IsNullOrWhiteSpace(product.Audience)
            || string.IsNullOrWhiteSpace(product.DisplayName) || !CapabilityCatalog.All.Any(x => x.Audience == product.Center))
            throw new ArgumentException("اطلاعات سامانه و مرکز رسمی الزامی است.");
        if (product.TenantId.HasValue && !Tenants().Any(x => x.Id == product.TenantId)) throw new ArgumentException("سازمان معتبر نیست.");
        if (product.Url is not null && (!Uri.TryCreate(product.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.EndsWith(".fanasa.net.local", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443))
            throw new ArgumentException("دامنه HTTPS داخلی فن‌آسا لازم است.");
        using var connection = Open(); using var command = connection.CreateCommand();
        var old = Products().SingleOrDefault(x => x.Key == product.Key);
        if (old is not null && (old.Owner != product.Owner || old.TenantId != product.TenantId)) throw new InvalidOperationException("مالکیت سامانه قابل تغییر نیست.");
        product = product with { Id = old?.Id ?? (product.Id == Guid.Empty ? Guid.NewGuid() : product.Id) };
        command.CommandText = "INSERT INTO platform_products(id,key,data) VALUES($id,$key,$data) ON CONFLICT(key) DO UPDATE SET data=excluded.data";
        command.Parameters.AddWithValue("$id", product.Id.ToString()); command.Parameters.AddWithValue("$key", product.Key);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(product, JsonOptions)); command.ExecuteNonQuery(); Audit(actor, "product.register", product);
        return product;
    }
    public Product RegisterLegacy(RegisterProductRequest request)
    {
        if (Products().Any(x => x.Key == request.Key)) throw new InvalidOperationException("A product with this key already exists.");
        var center = CapabilityCatalog.All.FirstOrDefault(x => x.Audience == request.CenterSlug || x.Name == request.CenterSlug) ?? throw new ArgumentException("Official capability center required.");
        var attributes = request.Attributes?.ToDictionary() ?? [];
        attributes.TryGetValue("url", out var url); attributes.TryGetValue("owner", out var owner);
        return Domain(Register(new(Guid.NewGuid(), request.Key, request.DisplayName, request.Audience, center.Audience, url, request.RepositoryUrl, null, owner ?? "platform", attributes), "catalog-api"));
    }
    public static Product Domain(PlatformProduct product) => new(product.Id, product.Key, product.DisplayName, product.Audience, product.Repository, product.Center,
        new Dictionary<string, string>(product.Attributes ?? []) { ["url"] = product.Url ?? "", ["owner"] = product.Owner });
    private void Audit(string actor, string operation, object value)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO platform_audit(actor,operation,data,recorded) VALUES($actor,$operation,$data,$time)";
        command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$operation", operation); command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(value, JsonOptions));
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }
}
