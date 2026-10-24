using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.ServiceCatalog;

public sealed record PublishedServiceView(Guid ProductId, string Key, string DisplayName, string Center,
    string Description, string? DetailsUrl, string? RequestAccessUrl, bool Published);
public sealed record ServicePublicationCommand(long ExpectedRevision, bool Published, string Description,
    string? DetailsUrl, string? RequestAccessUrl, string Reason);
public sealed record ServicePublication(Guid ProductId, string Key, string DisplayName, string Center,
    string Description, string? DetailsUrl, string? RequestAccessUrl, bool Published,
    long Revision, string Actor, string Reason, DateTimeOffset RecordedAt);

/// <summary>Approved public metadata references the existing registry and never grants access.</summary>
public sealed class PublishedServiceCatalog(PlatformRegistry registry, FabricDatabase database,
    IConfiguration configuration, TimeProvider time)
{
    public const string DocumentKind = "platform.service-publication.v1";
    public const string AuditKind = "PlatformServicePublicationChanged";
    public const string ReadScope = "platform.catalog";
    public const int MaxProducts = 2000;
    public const int MaxBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public ServicePublication? Read(Guid productId) => database.ReadSnapshot(() =>
    {
        var value = database.Read<ServicePublication>(DocumentKind, productId.ToString("D"));
        if (value is not null) ValidatePersisted(value, productId.ToString("D"), requireApprovedHost: false);
        return value;
    });

    public ServicePublication Save(Guid productId, ServicePublicationCommand command, string actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedRevision < 0 || string.IsNullOrWhiteSpace(actor) || actor.Length > 120)
            throw new ArgumentException("Invalid publication revision or authenticated actor.");
        var reason = Text(command.Reason, 500);
        var product = command.Published
            ? registry.Products().SingleOrDefault(x => x.Id == productId)
                ?? throw new KeyNotFoundException("The central product is not registered.")
            : null;
        if (product is not null) ValidateProduct(product);
        var description = command.Published ? Text(command.Description, 1600) : null;
        var details = command.Published ? Link(command.DetailsUrl) : null;
        var request = command.Published ? Link(command.RequestAccessUrl) : null;
        return database.Transaction(() =>
        {
            var key = productId.ToString("D");
            var previous = database.Read<ServicePublication>(DocumentKind, key);
            if (previous is not null) ValidatePersisted(previous, key, requireApprovedHost: false);
            if ((previous?.Revision ?? 0) != command.ExpectedRevision)
                throw new InvalidOperationException("Publication revision has changed.");
            var recordedAt = time.GetUtcNow();
            var revision = checked(command.ExpectedRevision + 1);
            ServicePublication value;
            if (product is not null)
                value = new(product.Id, product.Key, product.DisplayName, product.Center,
                    description!, details, request, true, revision, actor, reason, recordedAt);
            else
            {
                if (previous is null)
                    throw new KeyNotFoundException("There is no publication to withdraw.");
                // Withdrawal remains possible after ownership or official-link policy changes.
                value = previous with
                {
                    Published = false, DetailsUrl = null, RequestAccessUrl = null,
                    Revision = revision, Actor = actor, Reason = reason, RecordedAt = recordedAt
                };
            }
            database.Put(DocumentKind, key, value);
            database.Emit("platform-service-publication:" + key + ":" + value.Revision,
                AuditKind, value, value.RecordedAt);
            return value;
        });
    }

    public PublishedServiceView[] Catalog() => database.ReadSnapshot(() =>
    {
        var products = registry.Products().ToDictionary(x => x.Id);
        var result = new List<PublishedServiceView>();
        string? after = null;
        var scanned = 0;
        while (true)
        {
            var page = database.DocumentPage(DocumentKind, after, 100);
            foreach (var document in page)
            {
                if (++scanned > 10000) throw new InvalidDataException("Publication scan limit exceeded.");
                var value = JsonSerializer.Deserialize<ServicePublication>(document.Json)
                    ?? throw new InvalidDataException("Invalid service publication.");
                ValidatePersisted(value, document.Key, requireApprovedHost: value.Published);
                if (!value.Published) continue;
                if (!products.TryGetValue(value.ProductId, out var product)
                    || product.Key != value.Key || product.DisplayName != value.DisplayName || product.Center != value.Center)
                    throw new InvalidDataException("Publication no longer matches the central product.");
                ValidateProduct(product);
                if (result.Count == MaxProducts) throw new InvalidDataException("Published catalog limit exceeded.");
                result.Add(new(value.ProductId, value.Key, value.DisplayName, value.Center, value.Description,
                    value.DetailsUrl, value.RequestAccessUrl, true));
            }
            if (page.Length < 100) break;
            after = page[^1].Key;
        }
        var response = result.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        if (JsonSerializer.SerializeToUtf8Bytes(response, WebJson).Length > MaxBytes)
            throw new InvalidDataException("Published catalog transport limit exceeded.");
        return response;
    });

    private void ValidateProduct(PlatformProduct product)
    {
        if (product.Id == Guid.Empty || string.IsNullOrWhiteSpace(product.Key)
            || string.IsNullOrWhiteSpace(product.DisplayName) || product.DisplayName.Length > 160
            || !CapabilityCatalog.All.Any(x => x.Audience == product.Center)
            || product.TenantId is not { } tenant || !registry.Tenants().Any(x => x.Id == tenant))
            throw new ArgumentException("Publication requires a registered product with an active owner and official center.");
    }

    private void ValidatePersisted(ServicePublication value, string key, bool requireApprovedHost = true)
    {
        try
        {
            if (value.ProductId == Guid.Empty || value.ProductId.ToString("D") != key || value.Revision < 1
                || string.IsNullOrWhiteSpace(value.Key) || string.IsNullOrWhiteSpace(value.DisplayName)
                || value.DisplayName.Length > 160 || !CapabilityCatalog.All.Any(x => x.Audience == value.Center)
                || string.IsNullOrWhiteSpace(value.Actor) || value.Actor.Length > 120 || value.RecordedAt.Offset != TimeSpan.Zero)
                throw new ArgumentException("Invalid publication identity or audit metadata.");
            if (Text(value.Description, 1600) != value.Description || Text(value.Reason, 500) != value.Reason
                || Link(value.DetailsUrl, requireApprovedHost) != value.DetailsUrl || Link(value.RequestAccessUrl, requireApprovedHost) != value.RequestAccessUrl)
                throw new ArgumentException("Invalid publication metadata.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid persisted service publication.", error); }
    }

    private static string Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException("Bounded plain text is required.");
        return value.Trim();
    }

    private string? Link(string? value, bool requireApprovedHost = true)
    {
        if (value is null) return null;
        var allowed = configuration.GetSection("Platform:ServiceCatalog:OfficialLinkHosts").Get<string[]>() ?? [];
        if (value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\') || value != value.Trim()
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.HostNameType != UriHostNameType.Dns
            || requireApprovedHost && !allowed.Any(host => string.Equals(host, uri.IdnHost, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Links require an explicitly approved HTTPS host.");
        return uri.AbsoluteUri;
    }

    public static bool ReaderConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Platform:ServiceCatalog:ClientId"])
        && !string.IsNullOrWhiteSpace(configuration["Platform:ServiceCatalog:ClientProductKey"]);

    public static bool AllowsReader(ClaimsPrincipal user, IConfiguration configuration,
        PlatformRegistry registry, IAccessManagement access, TimeProvider? time = null)
    {
        if (!ReaderConfigured(configuration)) return false;
        var identities = user.Identities.Where(x => x.IsAuthenticated).ToArray();
        if (identities.Length != 1) return false;
        var identity = identities[0];
        var clients = identity.FindAll("azp").Select(x => x.Value).ToArray();
        var scopes = identity.FindAll("scope").Select(x => x.Value).ToArray();
        var subjects = identity.FindAll("sub").Select(x => x.Value).ToArray();
        if (clients.Length != 1 || clients[0] != configuration["Platform:ServiceCatalog:ClientId"] || scopes.Length != 1
            || !scopes[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ReadScope, StringComparer.Ordinal)
            || subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0])) return false;
        var now = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        bool NumericDate(string name, out long seconds)
        {
            var values = identity.FindAll(name).Select(x => x.Value).ToArray();
            seconds = 0;
            return values.Length == 1
                && long.TryParse(values[0], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds)
                && seconds >= 0
                && seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) == values[0];
        }
        var alternateClients = identity.FindAll("client_id").ToArray();
        var active = identity.FindAll("active").ToArray();
        if (!NumericDate("exp", out var expiry) || now >= expiry
            || identity.HasClaim(x => x.Type == "nbf")
                && (!NumericDate("nbf", out var start) || now < start)
            || alternateClients.Length > 1 || alternateClients.Any(x => x.Value != clients[0])
            || active.Length > 1 || active.Any(x => !x.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
            return false;
        var products = registry.Products().Where(x => x.Key == configuration["Platform:ServiceCatalog:ClientProductKey"]).Take(2).ToArray();
        if (products.Length != 1 || products[0].TenantId is not { } owner
            || access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1) return false;
        var registrations = access.GetClients(products[0].Id).Where(x => x.Key == clients[0]).Take(2).ToArray();
        return registrations.Length == 1 && registrations[0].IsActive
            && registrations[0].AllowedScopes?.Contains(ReadScope, StringComparer.Ordinal) == true;
    }
}

[ApiController, Route("api/platform/published-services"), Authorize(AuthenticationSchemes = "PlatformBearer")]
public sealed class PublishedServicesApi(PlatformRegistry registry, FabricDatabase database,
    IConfiguration configuration, TimeProvider time, IAccessManagement access) : ControllerBase
{
    [HttpGet]
    public IActionResult Read()
    {
        Response.Headers.CacheControl = "no-store";
        if (Request.Query.Count != 0) return BadRequest(new { error = "Catalog query parameters are not supported." });
        try
        {
            if (!PublishedServiceCatalog.ReaderConfigured(configuration))
                return StatusCode(503, new { error = "Published service catalog is not configured." });
            if (!PublishedServiceCatalog.AllowsReader(User, configuration, registry, access, time)) return Forbid("PlatformBearer");
            var catalog = new PublishedServiceCatalog(registry, database, configuration, time).Catalog();
            if (!PublishedServiceCatalog.AllowsReader(User, configuration, registry, access, time)) return Forbid("PlatformBearer");
            return Ok(catalog);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or JsonException or ArgumentException)
        { return StatusCode(503, new { error = "Published service catalog is unavailable." }); }
    }
}

[ApiController, Route("api/platform/service-publications/{productId:guid}")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
public sealed class ServicePublicationsApi(PlatformRegistry registry, FabricDatabase database,
    IConfiguration configuration, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public IActionResult Read(Guid productId)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(new PublishedServiceCatalog(registry, database, configuration, time).Read(productId)); }
        catch (Exception error) when (error is SqliteException or InvalidDataException or JsonException)
        { return StatusCode(503, new { error = "Service publication is unavailable." }); }
    }

    [HttpPut, ValidateAntiForgeryToken, RequestSizeLimit(16384)]
    public IActionResult Save(Guid productId, ServicePublicationCommand command)
    {
        Response.Headers.CacheControl = "no-store";
        if (!PlatformAuthorization.IsAdmin(User) || string.IsNullOrWhiteSpace(User.FindFirstValue("sub"))) return Forbid();
        try { return Ok(new PublishedServiceCatalog(registry, database, configuration, time).Save(productId, command, User.FindFirstValue("sub")!)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { error = "Publication metadata is invalid." }); }
        catch (InvalidOperationException) { return Conflict(new { error = "Publication revision has changed." }); }
        catch (Exception error) when (error is SqliteException or InvalidDataException or JsonException or OverflowException)
        { return StatusCode(503, new { error = "Service publication is unavailable." }); }
    }
}
