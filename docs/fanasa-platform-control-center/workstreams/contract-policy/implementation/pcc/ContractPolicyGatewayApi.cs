using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

/// <summary>Gateway's signed service delegation supplies identity and route assertions, never PIP evidence.</summary>
public sealed record ContractGatewayPolicyRequest(
    string SchemaVersion,
    string RequestId,
    string SubjectId,
    string ClientId,
    string ConsumerTenantId,
    string OwnerTenantId,
    string ProductId,
    string Environment,
    string Action,
    string OperationGroup,
    string Audience,
    string Scope,
    string AllocationId,
    string AllocationScope,
    decimal RequestedAt,
    long TokenExpiresAt);

/// <summary>Closed, non-coercing parser for the existing APISIX policy-owner transport.</summary>
public static class ContractGatewayPolicyRequestJson
{
    public const int MaximumPayloadCharacters = 65_536;
    public const int MaximumPayloadBytes = 262_144;
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "requestId", "subjectId", "clientId", "consumerTenantId", "ownerTenantId",
        "productId", "environment", "action", "operationGroup", "audience", "scope", "allocationId",
        "allocationScope", "requestedAt", "tokenExpiresAt"
    };

    public static bool TryParse(string payload, out ContractGatewayPolicyRequest? request)
    {
        request = null;
        if (payload.Length is 0 or > MaximumPayloadCharacters) return false;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Properties.Contains(property.Name) || !seen.Add(property.Name)) return false;
            if (seen.Count != Properties.Count) return false;
            static string? ReadString(JsonElement root, string name, int maximum = 512)
            {
                var value = root.GetProperty(name);
                return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                    && text.Length <= maximum && !text.Any(char.IsControl) ? text : null;
            }
            static bool Identifier(string? value) => value is not null && !value.Any(char.IsWhiteSpace);
            static bool Uuid(string? value) => Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty;
            var schema = ReadString(root, "schemaVersion");
            var requestId = ReadString(root, "requestId");
            var subject = ReadString(root, "subjectId");
            var client = ReadString(root, "clientId");
            var consumer = ReadString(root, "consumerTenantId");
            var owner = ReadString(root, "ownerTenantId");
            var product = ReadString(root, "productId");
            var environment = ReadString(root, "environment", 64);
            var action = ReadString(root, "action");
            var group = ReadString(root, "operationGroup", 64);
            var audience = ReadString(root, "audience");
            var scope = ReadString(root, "scope");
            var allocation = ReadString(root, "allocationId");
            var allocationScope = ReadString(root, "allocationScope", 13_000);
            static bool Slug(string? value) => value is { Length: > 0 } && value[0] is >= 'a' and <= 'z'
                && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
            if (schema != "fanasa.gateway-pdp.request.v2" || requestId is not { Length: 34 }
                || !requestId.StartsWith("g-", StringComparison.Ordinal)
                || requestId.AsSpan(2).ContainsAnyExcept("0123456789abcdef")
                || !Uuid(subject) || !Uuid(consumer) || !Uuid(owner)
                || !Identifier(client) || !Identifier(product) || !Slug(environment) || !Slug(group)
                || !Identifier(action) || !Identifier(audience) || !Identifier(scope) || !Identifier(allocation)
                || allocationScope is null) return false;
            var requested = root.GetProperty("requestedAt");
            var expiry = root.GetProperty("tokenExpiresAt");
            if (requested.ValueKind != JsonValueKind.Number || !requested.TryGetDecimal(out var requestedAt)
                || requestedAt <= 0 || requestedAt > 253_402_300_799m
                || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var tokenExpiresAt)
                || tokenExpiresAt is <= 0 or > 253_402_300_799
                || !string.Equals(tokenExpiresAt.ToString(CultureInfo.InvariantCulture), expiry.GetRawText(), StringComparison.Ordinal))
                return false;
            request = new(schema, requestId, subject!, client!, consumer!, owner!, product!, environment!, action!,
                group!, audience!, scope!, allocation!, allocationScope, requestedAt, tokenExpiresAt);
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

[ApiController, Route("api/gateway/decision")]
[Authorize(AuthenticationSchemes = ContractGatewayDecisionAuthentication.Scheme)]
public sealed class ContractGatewayDecisionApi(ContractGatewayDecisionService decisions,
    ContractGatewayDecisionAuthorization authorization) : ControllerBase
{
    [HttpPost, RequestSizeLimit(ContractGatewayPolicyRequestJson.MaximumPayloadBytes)]
    public async Task<IActionResult> Evaluate(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!authorization.Allows(User)) return Forbid(ContractGatewayDecisionAuthentication.Scheme);
        if (Request.ContentLength is > ContractGatewayPolicyRequestJson.MaximumPayloadBytes) return StatusCode(413);
        var buffer = new char[ContractGatewayPolicyRequestJson.MaximumPayloadCharacters + 1];
        var count = 0;
        try
        {
            using var reader = new StreamReader(Request.Body, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            while (count < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
        }
        catch (DecoderFallbackException) { return BadRequest(new { error = "invalid-gateway-request-json" }); }
        // An awaited read can outlive signed expiry or administrative revocation.
        if (!authorization.Allows(User)) return Forbid(ContractGatewayDecisionAuthentication.Scheme);
        if (count > ContractGatewayPolicyRequestJson.MaximumPayloadCharacters) return StatusCode(413);
        if (!ContractGatewayPolicyRequestJson.TryParse(new string(buffer, 0, count), out var request))
            return BadRequest(new { error = "invalid-gateway-request-json" });
        var decision = await decisions.EvaluateAsync(request!, cancellationToken);
        // No usable Permit is returned when the Gateway credential changes during trusted PIP reads.
        if (!authorization.Allows(User)) return Forbid(ContractGatewayDecisionAuthentication.Scheme);
        return Ok(decision);
    }
}
