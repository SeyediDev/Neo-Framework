using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Mcp;

public static class McpJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
}
public sealed record McpConnection(Uri ApiBaseUrl, Guid OrganizationId, Guid WorkspaceId, string ChatId, string TokenSecretRef, bool CompactResponses = true)
{
    public string ScopePath => $"api/orchestration/v1/organizations/{OrganizationId:D}/workspaces/{WorkspaceId:D}/";
    public static McpConnection Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("NeoMcp");
        if (!Uri.TryCreate(section["ApiBaseUrl"], UriKind.Absolute, out var url) ||
            (url.Scheme != "https" && !(url.Scheme == "http" && url.IsLoopback &&
                bool.TryParse(section["AllowLoopbackHttp"], out var local) && local)) ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) ||
            !Guid.TryParse(section["OrganizationId"], out var org) || org == Guid.Empty ||
            !Guid.TryParse(section["WorkspaceId"], out var workspace) || workspace == Guid.Empty)
            throw new ArgumentException("Invalid API destination or workspace scope.");
        var chat = section["ChatId"] ?? "";
        var reference = section["TokenSecretRef"] ?? "env:NEO_ORCHESTRATION_ACCESS_TOKEN";
        if (chat.Length is < 1 or > 200 || chat != chat.Trim() || chat.Any(char.IsControl) ||
            !Regex.IsMatch(reference, @"\Aenv:[A-Z][A-Z0-9_]{0,100}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid chat or secret reference.");
        if (!url.AbsolutePath.EndsWith('/')) url = new Uri(url.AbsoluteUri + "/");
        var compactSetting = section["CompactResponses"];
        var compact = true;
        if (compactSetting is not null && !bool.TryParse(compactSetting, out compact))
            throw new ArgumentException("Invalid CompactResponses setting.");
        return new(url, org, workspace, chat, reference, compact);
    }
}

// HTTP-only client: no SQL, local repository execution, token minting or policy bypass.
public sealed class McpApiClient(HttpClient http, McpConnection connection)
{
    public string Context() => JsonSerializer.Serialize(new {
        apiBaseUrl = connection.ApiBaseUrl.AbsoluteUri, connection.OrganizationId, connection.WorkspaceId, connection.ChatId,
        authority = "API issuer subject and workspace grants; these settings do not grant access",
        transport = "stdio", execution = "No shell/model execution in this MCP server",
        compactResponses = connection.CompactResponses
    }, McpJson.Options);

    public async Task<string> Send<T>(string resource, CancellationToken ct, object? body = null, HttpMethod? method = null)
    {
        if (resource.StartsWith('/') || resource.Contains("://") || resource.Contains('\\') ||
            resource.Split('?', 2)[0].Split('/').Contains(".."))
            throw new McpException("invalid-resource");
        var token = Environment.GetEnvironmentVariable(connection.TokenSecretRef[4..]);
        if (token is null || token.Length is < 32 or > 32768 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new McpException("credential-unavailable: supply a private issuer access token and restart the MCP process.");
        using var request = new HttpRequestMessage(method ?? (body is null ? HttpMethod.Get : HttpMethod.Post),
            new Uri(connection.ApiBaseUrl, connection.ScopePath + resource));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Orchestration-Chat", connection.ChatId);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, McpJson.Options);
            if (Encoding.UTF8.GetByteCount(json) > 262144) throw new McpException("invalid-input: request exceeds 256 KiB.");
            if (json.Contains(token, StringComparison.Ordinal)) throw new McpException("invalid-input: request contains the access credential.");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        try
        {
            // No automatic command replay and no redirects carrying the credential.
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw Failure((int)response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (json.Contains(token, StringComparison.Ordinal)) throw new McpException("unsafe-response: credential reflection rejected.");
            var value = JsonSerializer.Deserialize<T>(json, McpJson.Options) ?? throw new JsonException();
            if (body is not null && connection.CompactResponses && value is WorkItemDetails details)
                return JsonSerializer.Serialize(WorkContextProjection.Receipt(details), McpJson.Options);
            return JsonSerializer.Serialize(value, McpJson.Options);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new McpException("transport-unavailable: outcome is unconfirmed; read current state before retrying. No automatic replay occurred."); }
    }
    private static McpException Failure(int status) => new(status switch {
        400 => "api-400: invalid input; check the tool schema.",
        401 => "api-401: issuer token missing, expired or invalid; renew privately and restart.",
        403 => "api-403: workspace or operation permission denied; do not bypass.",
        404 => "api-404: resource not found in the configured workspace.",
        409 => "api-409: stale version, ownership or state conflict; reload and reassess before retrying.",
        503 => "api-503: storage or execution provider unavailable; inspect configuration and current state.",
        _ => "api-unconfirmed: request was not confirmed; inspect current state before retrying."
    });
}
