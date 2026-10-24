using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.Platform;

public sealed record DeveloperProjectRegistrationRequest(Guid ProjectId, Guid ComponentId, Guid ApplicationId,
    Guid TenantId, string Owner, string Actor, string DisplayName, string ComponentName, string Repository, string Environment);
public sealed record DeveloperProjectEntryPoint(Guid ComponentId, string ComponentName, Guid ApplicationId, string Environment, string Url);

public static class DeveloperProjectRegistration
{
    public const string EntryPointsAttribute = "developer.entrypoints";
    public const string ProjectAttribute = "developer.projectId";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string Permission(string environment, string operation) => environment switch
    {
        "development" => "developer." + operation,
        "staging" or "production" => "developer." + environment + "." + operation,
        _ => throw new ArgumentException("Invalid deployment environment.")
    };

    public static bool IsProject(PlatformProduct product) => product.Attributes?.ContainsKey(ProjectAttribute) == true;

    public static PlatformProduct ForSubject(PlatformProduct product, string subject, PlatformRegistry registry)
    {
        if (!IsProject(product)) return product;
        var attributes = new Dictionary<string, string>(product.Attributes!);
        var entries = ReadEntries(product).Where(x => product.TenantId.HasValue &&
            registry.Allows(subject, product.TenantId.Value, Permission(x.Environment, "read"))).ToArray();
        attributes[EntryPointsAttribute] = JsonSerializer.Serialize(entries, JsonOptions);
        return product with { Attributes = attributes };
    }

    public static PlatformProduct Register(DeveloperProjectRegistrationRequest request, string serviceActor,
        PlatformRegistry registry, IConfiguration configuration)
    {
        Validate(request, configuration);
        if (!registry.Allows(request.Owner, request.TenantId, "developer.read") ||
            !registry.Allows(request.Actor, request.TenantId, Permission(request.Environment, "launch")))
            throw new UnauthorizedAccessException("Active owner membership and actor launch permission are required.");
        var path = configuration["Platform:RegistryPath"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "platform-registry.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        // The immediate transaction serializes merges and cross-project slot checks across service processes.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT data FROM platform_products";
        var products = new List<PlatformProduct>();
        using (var reader = read.ExecuteReader())
            while (reader.Read()) products.Add(JsonSerializer.Deserialize<PlatformProduct>(reader.GetString(0), JsonOptions)!);
        var key = "developer-" + request.ProjectId.ToString("N");
        var existing = products.SingleOrDefault(x => x.Key == key);
        if (existing is not null && (!IsProject(existing) || existing.Owner != request.Owner ||
            existing.TenantId != request.TenantId || existing.Repository != request.Repository ||
            existing.Attributes![ProjectAttribute] != request.ProjectId.ToString("D")))
            throw new InvalidOperationException("Project identity, ownership, tenant and repository cannot be replaced.");
        var entries = existing is null ? new List<DeveloperProjectEntryPoint>() : ReadEntries(existing).ToList();
        foreach (var product in products)
        {
            if (!IsProject(product))
            {
                if (product.Key == "developer-" + request.ApplicationId.ToString("N"))
                    throw new InvalidOperationException("Application identity is already registered as a legacy product.");
                continue;
            }
            foreach (var entry in ReadEntries(product))
            {
                if (entry.ApplicationId == request.ApplicationId &&
                    (product.Key != key || entry.ComponentId != request.ComponentId || entry.Environment != request.Environment))
                    throw new InvalidOperationException("Application identity is already assigned to a different deployment slot.");
                if (entry.ComponentId == request.ComponentId && product.Key != key)
                    throw new InvalidOperationException("Component identity is already assigned to another project.");
            }
        }
        if (entries.Any(x => x.ComponentId == request.ComponentId && x.ComponentName != request.ComponentName) ||
            entries.Any(x => x.ComponentId != request.ComponentId && string.Equals(x.ComponentName, request.ComponentName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Component identity or name cannot be replaced.");
        var slot = entries.SingleOrDefault(x => x.ComponentId == request.ComponentId && x.Environment == request.Environment);
        if (slot is not null && slot.ApplicationId != request.ApplicationId)
            throw new InvalidOperationException("Deployment slot application identity cannot be replaced.");
        if (slot is null)
            entries.Add(new(request.ComponentId, request.ComponentName, request.ApplicationId, request.Environment,
                "https://developer.fanasa.net.local/runtime/" + request.ApplicationId.ToString("D") + "/"));
        if (entries.Select(x => x.ComponentId).Distinct().Count() > 32 || entries.Count > 96)
            throw new ArgumentException("A project supports at most 32 components and three environments per component.");
        var attributes = new Dictionary<string, string>(existing?.Attributes ?? [])
        {
            [ProjectAttribute] = request.ProjectId.ToString("D"),
            [EntryPointsAttribute] = JsonSerializer.Serialize(entries.OrderBy(x => x.ComponentId).ThenBy(x => x.Environment), JsonOptions)
        };
        var productResult = new PlatformProduct(existing?.Id ?? Guid.NewGuid(), key, request.DisplayName,
            "fanasa.app." + request.ProjectId.ToString("N"), "fanasa.rayan",
            "https://developer.fanasa.net.local/?project=" + request.ProjectId.ToString("D"),
            request.Repository, request.TenantId, request.Owner, attributes);
        using var save = connection.CreateCommand(); save.Transaction = transaction;
        save.CommandText = "INSERT INTO platform_products(id,key,data) VALUES($id,$key,$data) ON CONFLICT(key) DO UPDATE SET data=excluded.data";
        save.Parameters.AddWithValue("$id", productResult.Id.ToString()); save.Parameters.AddWithValue("$key", key);
        save.Parameters.AddWithValue("$data", JsonSerializer.Serialize(productResult, JsonOptions)); save.ExecuteNonQuery();
        using var audit = connection.CreateCommand(); audit.Transaction = transaction;
        audit.CommandText = "INSERT INTO platform_audit(actor,operation,data,recorded) VALUES($actor,'developer.project.register',$data,$time)";
        audit.Parameters.AddWithValue("$actor", serviceActor);
        audit.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new { request.ProjectId, request.ComponentId,
            request.ApplicationId, request.TenantId, request.Owner, request.Actor, request.Environment }, JsonOptions));
        audit.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O")); audit.ExecuteNonQuery();
        transaction.Commit();
        return productResult;
    }

    public static DeveloperProjectEntryPoint[] ReadEntries(PlatformProduct product)
    {
        if (product.Attributes?.TryGetValue(EntryPointsAttribute, out var raw) != true) return [];
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Invalid stored project entrypoints.");
        try
        {
            var entries = JsonSerializer.Deserialize<DeveloperProjectEntryPoint[]>(raw, JsonOptions) ?? [];
            if (entries.Length > 96 || entries.Any(x => x is null || x.ComponentId == Guid.Empty || x.ApplicationId == Guid.Empty ||
                x.Environment is not ("development" or "staging" or "production")))
                throw new InvalidOperationException("Invalid stored project entrypoints.");
            return entries;
        }
        catch (JsonException ex) { throw new InvalidOperationException("Invalid stored project entrypoints.", ex); }
    }

    private static void Validate(DeveloperProjectRegistrationRequest request, IConfiguration configuration)
    {
        if (request.ProjectId == Guid.Empty || request.ComponentId == Guid.Empty || request.ApplicationId == Guid.Empty || request.TenantId == Guid.Empty)
            throw new ArgumentException("Project, component, application and tenant identities are required.");
        _ = Permission(request.Environment, "launch");
        foreach (var text in new[] { request.Owner, request.Actor })
            if (string.IsNullOrWhiteSpace(text) || text.Length > 120 || text.Any(char.IsControl)) throw new ArgumentException("Invalid subject.");
        foreach (var text in new[] { request.DisplayName, request.ComponentName })
            if (string.IsNullOrWhiteSpace(text) || text.Length > 120 || text.Any(char.IsControl) || text != text.Trim())
                throw new ArgumentException("Invalid project or component name.");
        var allowed = configuration.GetSection("Platform:DeveloperGitHosts").GetChildren().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (allowed.Length == 0) allowed = ["github.com", "gitlab.com", "bitbucket.org"];
        if (string.IsNullOrWhiteSpace(request.Repository) || request.Repository.Length > 2048 || request.Repository != request.Repository.Trim() || request.Repository.Any(char.IsControl) ||
            !Uri.TryCreate(request.Repository, UriKind.Absolute, out var repository) || repository.Scheme != "https" ||
            repository.Port != 443 || !string.IsNullOrEmpty(repository.UserInfo) || !string.IsNullOrEmpty(repository.Query) ||
            !string.IsNullOrEmpty(repository.Fragment) || repository.AbsolutePath.Trim('/').Split('/').Length < 2 ||
            !allowed.Contains(repository.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("An allowlisted HTTPS Git repository without credentials is required.");
    }
}
