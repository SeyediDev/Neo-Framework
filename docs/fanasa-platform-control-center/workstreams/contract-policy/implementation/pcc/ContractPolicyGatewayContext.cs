using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.ContractPolicy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

/// <summary>Explicit receiver configuration. No fixture, forwarded certificate or default key establishes authority.</summary>
public sealed class ContractSignedGatewayRequestOptions
{
    public bool Enabled { get; init; }
    public int MaximumBodyBytes { get; init; } = 65536;
    public ContractGatewayContextKey[] Keys { get; init; } = [];
    public bool IsConfigured => Enabled && MaximumBodyBytes is > 0 and <= 1048576
        && Keys is { Length: > 0 and <= 32 } && Keys.All(key => key is not null && key.IsConfigured)
        && Keys.Select(key => (key.KeyId, key.Tier)).Distinct().Count() == Keys.Length;
}

public sealed class ContractGatewayContextKey
{
    public string? KeyId { get; init; }
    public string? Tier { get; init; }
    public string? Secret { get; init; }
    public string? ClientCertificateSha256 { get; init; }
    internal bool IsConfigured => GatewayContextWire.Identifier(KeyId, 64)
        && Tier is "edge" or "internal" && GatewayContextWire.ValidSecret(Secret)
        && GatewayContextWire.Hex(ClientCertificateSha256, 64, lowercase: false);
}

public static class ContractGatewayContextServices
{
    public static IServiceCollection AddContractGatewayContextServices(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("ContractPolicy:GatewayRequest").Get<ContractSignedGatewayRequestOptions>() ?? new();
        if (options.Enabled && configuration.GetValue<bool>("ContractPolicy:GatewayContext:Enabled"))
            throw new InvalidOperationException("Only one ContractPolicy Gateway receiver mode may be enabled.");
        services.TryAddSingleton(options);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ContractGatewayContextResourceFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<ContractGatewayContextResourceFilter>());
        if (options.Enabled)
        {
            services.TryAddSingleton<GatewayContractPolicyObligationExecutor>();
            services.TryAddSingleton<IContractPolicyObligationExecutor>(provider => provider.GetRequiredService<GatewayContractPolicyObligationExecutor>());
            services.TryAddSingleton<IContractPolicyGatewayCommitGuard>(provider =>
                provider.GetRequiredService<IContractPolicyObligationExecutor>() as IContractPolicyGatewayCommitGuard
                ?? throw new InvalidOperationException("The enabled signed Gateway receiver requires its matching commit guard."));
        }
        return services;
    }

    /// <summary>Can be used by a host that still registers the unavailable executor with TryAdd.</summary>
    public static IServiceCollection AddContractPolicyHostWithGatewayContext(this IServiceCollection services, IConfiguration configuration)
        => services.AddContractGatewayContextServices(configuration)
            .AddContractGatewayDecisionServices(configuration).AddContractPolicyHost(configuration);
}

/// <summary>Captures the exact bounded bytes before MVC model binding and publishes only a verified immutable server feature.</summary>
public sealed class ContractGatewayContextResourceFilter(ContractSignedGatewayRequestOptions options, TimeProvider time) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerTypeInfo.AsType() != typeof(OrganizationOperationApi)
            || action.MethodInfo.Name != nameof(OrganizationOperationApi.Execute) || !options.Enabled)
        {
            await next();
            return;
        }
        var http = context.HttpContext;
        http.Features.Set<TrustedContractGatewayWitness>(null);
        GatewayContractPolicyObligationExecutor.ClearAdmittedProof(http);
        http.Response.Headers.CacheControl = "no-store";
        if (!options.IsConfigured)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }
        var originalBody = http.Request.Body;
        MemoryStream? captured = null;
        try
        {
            if (http.Request.ContentLength > options.MaximumBodyBytes)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
                return;
            }
            // The bound applies to chunked bodies as well as declared content lengths.
            captured = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await originalBody.ReadAsync(buffer, http.RequestAborted);
                if (count == 0) break;
                if (captured.Length + count > options.MaximumBodyBytes)
                {
                    context.Result = new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
                    return;
                }
                captured.Write(buffer, 0, count);
            }
            var bytes = captured.ToArray();
            var witness = await GatewayContextWire.VerifyAsync(http, bytes, options, time);
            if (witness is null)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                return;
            }
            captured.Position = 0;
            http.Request.Body = captured;
            http.Features.Set(witness);
            await next();
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException
            or IOException or InvalidOperationException or CryptographicException)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
        finally
        {
            http.Features.Set<TrustedContractGatewayWitness>(null);
            GatewayContractPolicyObligationExecutor.ClearAdmittedProof(http);
            http.Request.Body = originalBody;
            captured?.Dispose();
        }
    }
}

internal sealed record GatewayResourceTenant(string ConsumerTenantId, string ProductId, string Environment, string Action);
internal sealed record GatewayAuthorizedContext(string RequestId, string DecisionId, string SubjectId, string ClientId,
    string ConsumerTenantId, string OwnerTenantId, string ProductId, string Environment, string Action,
    string PolicyVersion, string ContractId, long ContractVersion, string AllocationId, string AllocationScope,
    long ConfigRevision, string AllocationPeriodId, long PeriodStartMs, long PeriodEndMs,
    GatewayResourceTenant ResourceTenant, decimal DecisionValidUntil, long TokenExpiresAt, string AdmissionId);
internal sealed record TrustedContractGatewayWitness(string KeyId, string Tier, string CertificateSha256,
    decimal IssuedAt, decimal ExpiresAt, string Method, string RawTarget, string Authorization,
    string IdempotencyKey, string BodySha256, OrgCommand Command, GatewayAuthorizedContext Context);

/// <summary>Called only within the shared Fabric transaction, after the writer lock and immediately before commit.</summary>
public interface IContractPolicyGatewayCommitGuard
{
    void ValidateCommit(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal? principal);
}

/// <summary>Consumes confirmed Gateway evidence once. This receiver does not provision or execute a limiter, budget or ledger.</summary>
public sealed class GatewayContractPolicyObligationExecutor(ContractSignedGatewayRequestOptions options,
    ContractPolicyOrganizationOptions organization, OrganizationBearerOptions bearer, IHttpContextAccessor accessor,
    FabricDatabase database, ContractPolicyStore projections, OrganizationUsageMetering metering,
    OrganizationContractPolicyDecisionService decisions, ContractPolicyModule module, TimeProvider time)
    : IContractPolicyObligationExecutor, IContractPolicyGatewayCommitGuard
{
    public bool IsConfigured => options.IsConfigured && metering.Enabled
        && metering.GatewayEnvironment == organization.Environment;
    private const string ReplayBucket = "contract-policy.gateway-context.replay.v1";
    private sealed record ReplayReceipt(string Tier, string RequestId, string KeyId, decimal ExpiresAt);
    private sealed class AdmittedProof(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal principal,
        PolicyDecision decision, TrustedContractGatewayWitness witness, bool isCommittedReceiptRetry)
    {
        public Guid Tenant { get; } = tenant;
        public string Actor { get; } = actor;
        public OrgCommand Command { get; } = command;
        public ClaimsPrincipal Principal { get; } = new(principal.Identities.Select(identity =>
            new ClaimsIdentity(identity.Claims.Select(claim => claim.Clone()), identity.AuthenticationType,
                identity.NameClaimType, identity.RoleClaimType)));
        public PolicyDecision Decision { get; } = ContractJson.Copy(decision);
        public TrustedContractGatewayWitness Witness { get; } = witness;
        public bool IsCommittedReceiptRetry { get; } = isCommittedReceiptRetry;
    }
    private static readonly string[] CompletedIds = [ContractPolicyEvaluator.AdmissionObligation, ContractPolicyEvaluator.ResourceObligation];
    internal static void ClearAdmittedProof(HttpContext http) => http.Features.Set<AdmittedProof>(null);

    public async ValueTask<IReadOnlyCollection<string>?> FulfillAsync(Guid tenant, string actor, OrgCommand command,
        PolicyDecision decision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var http = accessor.HttpContext;
        if (!IsConfigured || !organization.IsConfigured || !bearer.IsConfigured || http is null
            || http.Features.Get<TrustedContractGatewayWitness>() is not { } witness) return null;
        try
        {
            ClearAdmittedProof(http);
            // Authenticate the original bearer with the named handler; a cookie or arbitrary User identity cannot complete obligations.
            var authenticated = await http.AuthenticateAsync(OrganizationBearerSecurity.Scheme);
            cancellationToken.ThrowIfCancellationRequested();
            if (!authenticated.Succeeded || authenticated.Principal is null
                || !await GatewayContextWire.CertificateMatchesAsync(http, witness.CertificateSha256, time)
                || !Matches(tenant, actor, command, decision, authenticated.Principal, http, witness)) return null;
            var isCommittedReceiptRetry = false;
            var claimed = database.Transaction(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Matches(tenant, actor, command, decision, authenticated.Principal, http, witness)
                    || !CurrentBindingsMatch(tenant, actor, command, decision, authenticated.Principal, witness)) return false;
                isCommittedReceiptRetry = database.Read<OrgReceipt>("organization.receipt", $"{tenant}:{command.RequestId}") is not null;
                var context = witness.Context;
                var key = witness.Tier + ":" + context.RequestId;
                // BEGIN IMMEDIATE serializes claim attempts across connections; accepted claims survive a restart and key rotation.
                if (database.Read<ReplayReceipt>(ReplayBucket, key) is not null) return false;
                database.Put(ReplayBucket, key, new ReplayReceipt(witness.Tier, context.RequestId, witness.KeyId, witness.ExpiresAt));
                // A late veto retains the consumed nonce. This receiver never refunds or retries Gateway allocation consumption.
                return !cancellationToken.IsCancellationRequested
                    && Matches(tenant, actor, command, decision, authenticated.Principal, http, witness);
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (!claimed || !Matches(tenant, actor, command, decision, authenticated.Principal, http, witness)) return null;
            http.Features.Set(new AdmittedProof(tenant, actor, command, authenticated.Principal, decision, witness, isCommittedReceiptRetry));
            return Array.AsReadOnly(CompletedIds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    public void ValidateCommit(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal? principal)
    {
        var http = accessor.HttpContext;
        if (!database.IsInTransaction || !IsConfigured || http is null || principal is null || http.RequestAborted.IsCancellationRequested
            || http.Features.Get<AdmittedProof>() is not { } proof
            || http.Features.Get<TrustedContractGatewayWitness>() is not { } witness
            || !ReferenceEquals(witness, proof.Witness) || proof.Tenant != tenant || proof.Actor != actor || proof.Command != command)
            throw new UnauthorizedAccessException("A current admitted Gateway request is required at commit.");
        try
        {
            if (!ContractTokenIdentity.TryRead(principal, time, out var identity, out _, out _)
                || !ContractTokenIdentity.TryRead(proof.Principal, time, out var originalIdentity, out _, out _)
                || !SameIdentity(identity, originalIdentity)
                || !GatewayContextWire.CertificateMatches(http, witness.CertificateSha256, time)
                || !Matches(tenant, actor, command, proof.Decision, proof.Principal, http, witness))
                throw new UnauthorizedAccessException("The admitted Gateway request is no longer current.");
            // FabricDatabase is thread-local: a transaction guard must never await or synchronously block an asynchronous source.
            var pending = decisions.EvaluateAsync(tenant, proof.Principal, proof.Decision.RequestId, http.RequestAborted);
            if (!pending.IsCompletedSuccessfully)
                throw new UnauthorizedAccessException("Synchronous authoritative policy evidence is unavailable at commit.");
            var fresh = pending.Result;
            if (!Matches(tenant, actor, command, fresh, proof.Principal, http, witness)
                || !FabricOrganizationAccessSnapshotSource.Equivalent(
                    fresh with { EvaluatedAt = null, ValidUntil = null }, proof.Decision with { EvaluatedAt = null, ValidUntil = null })
                || !CurrentBindingsMatch(tenant, actor, command, fresh, proof.Principal, witness, proof.IsCommittedReceiptRetry)
                || !module.Pep.CanExecute(proof.Decision, CompletedIds, FabricOrganizationAccessSnapshotSource.Format(time.GetUtcNow()))
                || !GatewayContextWire.CertificateMatches(http, witness.CertificateSha256, time)
                || !Matches(tenant, actor, command, proof.Decision, proof.Principal, http, witness)
                || http.RequestAborted.IsCancellationRequested)
                throw new UnauthorizedAccessException("Gateway policy, allocation or consumption binding changed before commit.");
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception error)
        {
            throw new UnauthorizedAccessException("Authoritative Gateway evidence is unavailable at commit.", error);
        }
    }

    private static bool SameIdentity(ClaimsIdentity identity, ClaimsIdentity original)
        => identity.AuthenticationType == original.AuthenticationType
            && identity.Claims.Select(claim => (claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer))
                .SequenceEqual(original.Claims.Select(claim => (claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer)));

    private bool CurrentBindingsMatch(Guid tenant, string actor, OrgCommand command, PolicyDecision decision,
        ClaimsPrincipal principal, TrustedContractGatewayWitness witness, bool? committedReceiptRetry = null)
    {
        if (!database.IsInTransaction || !IsConfigured) return false;
        var projection = projections.Find(tenant);
        var context = witness.Context;
        if (projection is null || projection.SchemaVersion != "2.0.0"
            || projection.OwnerTenantId != context.OwnerTenantId || projection.ConsumerTenantId != context.ConsumerTenantId
            || projection.ProductId != context.ProductId || projection.ContractId != context.ContractId
            || projection.ContractVersion != context.ContractVersion || projection.Status != "Active"
            || projection.Policy.PolicyId + "@" + projection.Policy.PolicyVersion.ToString(CultureInfo.InvariantCulture) != context.PolicyVersion
            || projection.Limits.AllocationId != context.AllocationId
            || projection.Limits.CanonicalScope != context.AllocationScope || projection.Limits.ConfigRevision != context.ConfigRevision
            || !LimitsMatch(decision, projection.Limits)) return false;
        var receipt = database.Read<OrgReceipt>("organization.receipt", $"{tenant}:{command.RequestId}");
        if (committedReceiptRetry == true && receipt is null) return false;
        if (committedReceiptRetry != false && receipt is not null)
        {
            if (receipt.Actor != actor || receipt.Fingerprint != JsonSerializer.Serialize(command with { ExpectedRevision = 0 })
                || receipt.Revision <= 0) return false;
            var history = database.Read<OrgState>("organization.history", $"{tenant}:{receipt.Revision}");
            if (history is null || history.TenantId != tenant || history.Revision != receipt.Revision
                || history.Changes.LastOrDefault() is not { } change || change.Revision != receipt.Revision
                || change.Operation != "unit.save" || change.Actor != actor) return false;
            // The exact committed result is returned unchanged. Fresh access still applies, while a retired price cannot reprice it.
            metering.ValidateInvocation(tenant, command, actor, principal);
            return true;
        }
        var binding = metering.ReadGatewayBinding(tenant, actor, command, principal);
        if (binding.ConsumerTenantId != tenant || binding.ContractId != context.ContractId
            || binding.ContractVersion != context.ContractVersion || binding.AllocationId != context.AllocationId
            || binding.AllocationPeriodId != context.AllocationPeriodId
            || binding.PricingReference != projection.PricingReference.PriceId
            || binding.RateSnapshot.RateVersion != projection.PricingReference.PriceVersion) return false;
        var state = database.Read<OrgState>("organization", tenant.ToString("D"));
        return (!command.Id.HasValue || state?.Units.Any(unit => unit.Id == command.Id.Value) == true)
            && (!command.ParentId.HasValue || state?.Units.Any(unit => unit.Id == command.ParentId.Value && unit.Active) == true);
    }

    private bool Matches(Guid tenant, string actor, OrgCommand command, PolicyDecision decision, ClaimsPrincipal principal,
        HttpContext http, TrustedContractGatewayWitness witness)
    {
        var context = witness.Context;
        var now = GatewayContextWire.Epoch(time.GetUtcNow());
        if (!options.IsConfigured || !organization.IsConfigured || !bearer.IsConfigured
            || now < witness.IssuedAt || now >= witness.ExpiresAt || now >= context.DecisionValidUntil
            || now >= context.TokenExpiresAt || now * 1000 < context.PeriodStartMs || now * 1000 >= context.PeriodEndMs
            || !GatewayContextWire.HasDirectTls(http)
            || http.Request.Method != witness.Method || http.Features.Get<IHttpRequestFeature>()?.RawTarget != witness.RawTarget
            || !GatewayContextWire.ReadHeader(http, "Authorization", true, out var authorization) || authorization != witness.Authorization
            || !GatewayContextWire.ReadHeader(http, "Idempotency-Key", false, out var idempotency) || idempotency != witness.IdempotencyKey
            || !ContractTokenIdentity.TryRead(principal, time, out var identity, out var subject, out var expires)
            || subject != actor || subject != context.SubjectId || expires.ToUnixTimeSeconds() != context.TokenExpiresAt
            || tenant == Guid.Empty || !Guid.TryParseExact(context.ConsumerTenantId, "D", out var consumer)
            || tenant != consumer || command != witness.Command
            || command.Operation != "unit.save" || !Guid.TryParseExact(command.RequestId, "D", out var businessRequest) || businessRequest == Guid.Empty
            || context.ProductId != OrganizationContractPolicyDecisionService.Product || context.Action != OrganizationContractPolicyDecisionService.Action
            || context.Environment != organization.Environment || decision.SchemaVersion != "2.0.0"
            || decision.ProfileId != "fanasa.contract-policy/2" || decision.Decision != "Permit"
            || !ContractJson.IsCanonicalUtc(decision.ValidUntil) || !ContractJson.IsCanonicalUtc(decision.EvaluatedAt)
            || !DateTimeOffset.TryParseExact(decision.ValidUntil, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var deadline)
            || witness.ExpiresAt > GatewayContextWire.Epoch(deadline) || now >= GatewayContextWire.Epoch(deadline)
            || decision.PolicyReferences.Count != 1 || decision.ProjectionReference is not { } projection
            || projection.ContractId != context.ContractId || projection.ContractVersion != context.ContractVersion
            || decision.PolicyReferences[0].PolicyId + "@" + decision.PolicyReferences[0].PolicyVersion.ToString(CultureInfo.InvariantCulture) != context.PolicyVersion
            || decision.AllocationReference is not { } allocation || allocation.AllocationId != context.AllocationId
            || allocation.CanonicalScope != context.AllocationScope || decision.Obligations.Count != 2) return false;
        var selected = options.Keys.Where(key => key.KeyId == witness.KeyId && key.Tier == witness.Tier).ToArray();
        if (selected.Length != 1 || !string.Equals(selected[0].ClientCertificateSha256, witness.CertificateSha256, StringComparison.OrdinalIgnoreCase)) return false;
        static string? Single(ClaimsIdentity candidate, string name)
        {
            var values = candidate.Claims.Where(claim => claim.Type == name).Take(2).ToArray();
            return values.Length == 1 ? values[0].Value : null;
        }
        if (Single(identity, "iss") != bearer.Authority || Single(identity, "azp") != bearer.ClientId
            || bearer.ClientId != context.ClientId || bearer.Audience != OrganizationContractPolicyDecisionService.Audience
            || bearer.Scope != context.Action || !identity.Claims.Any(claim => claim.Type == "aud" && claim.Value == bearer.Audience)
            || identity.Claims.Any(claim => claim.Type == "client_id" && claim.Value != context.ClientId)
            || identity.Claims.Any(claim => claim.Type == "active" && claim.Value.Equals("false", StringComparison.OrdinalIgnoreCase))
            || Single(identity, "scope") is not { } scope || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(context.Action, StringComparer.Ordinal)) return false;
        if (identity.Claims.Any(claim => claim.Type == "consumerTenantId" && claim.Value != context.ConsumerTenantId)) return false;
        var admission = decision.Obligations.Where(obligation => obligation.Id == ContractPolicyEvaluator.AdmissionObligation).ToArray();
        var resource = decision.Obligations.Where(obligation => obligation.Id == ContractPolicyEvaluator.ResourceObligation).ToArray();
        if (admission.Length != 1 || resource.Length != 1 || admission[0].FulfillOn != "Permit" || resource[0].FulfillOn != "Permit") return false;
        using var resourceJson = JsonDocument.Parse(resource[0].Parameters.ToJsonString());
        if (!GatewayContextWire.OnlyKeys(resourceJson.RootElement, GatewayContextWire.ResourceKeys)) return false;
        return resourceJson.RootElement.Deserialize<GatewayResourceTenant>(GatewayContextWire.JsonOptions) == context.ResourceTenant;
    }

    private static bool LimitsMatch(PolicyDecision decision, AllocationLimits expected)
    {
        var obligation = decision.Obligations.SingleOrDefault(value => value.Id == ContractPolicyEvaluator.AdmissionObligation);
        if (obligation is null) return false;
        var actual = obligation.Parameters.Deserialize<AllocationLimits>(ContractJson.Options);
        return actual is not null && FabricOrganizationAccessSnapshotSource.Equivalent(expected, actual);
    }
}

internal static class GatewayContextWire
{
    private const decimal MaximumEpochSeconds = 253402300799m;
    private const long MaximumSafeInteger = 9007199254740991;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    internal static readonly string[] ResourceKeys = ["consumerTenantId", "productId", "environment", "action"];
    private static readonly string[] EnvelopeKeys = ["version", "keyId", "tier", "issuedAt", "expiresAt", "context"];
    private static readonly string[] ContextKeys = ["requestId", "decisionId", "subjectId", "clientId", "consumerTenantId",
        "ownerTenantId", "productId", "environment", "action", "policyVersion", "contractId", "contractVersion",
        "allocationId", "allocationScope", "configRevision", "allocationPeriodId", "periodStartMs", "periodEndMs",
        "resourceTenant", "decisionValidUntil", "tokenExpiresAt", "admissionId", "allowed", "confirmed"];
    private static readonly HashSet<string> CommandKeys = new(["expectedRevision", "operation", "id", "name", "code", "parentId",
        "kind", "unitId", "capacity", "positionId", "subject", "from", "to", "reason", "roleId", "requestId"], StringComparer.OrdinalIgnoreCase);

    internal static async ValueTask<TrustedContractGatewayWitness?> VerifyAsync(HttpContext http, byte[] body,
        ContractSignedGatewayRequestOptions options, TimeProvider time)
    {
        if (!options.IsConfigured || body.Length == 0 || body.Length > options.MaximumBodyBytes
            || http.Request.Method != "POST" || !HasDirectTls(http)
            || http.Features.Get<IHttpRequestFeature>()?.RawTarget is not { } target || !target.StartsWith('/')
            || target.Length > 8192 || target.Any(character => character < 32 || character == 127)
            || !ReadHeader(http, "X-Fanasa-Context", true, out var encoded) || encoded.Length > 21848
            || !ReadHeader(http, "X-Fanasa-Context-Signature", true, out var signature) || !Hex(signature, 64, lowercase: true)
            || !ReadHeader(http, "Authorization", true, out var authorization)
            || !ValidAuthorization(authorization)
            || !ReadHeader(http, "Idempotency-Key", false, out var idempotency) || !ValidIdempotency(idempotency)) return null;
        foreach (var header in http.Request.Headers.Keys)
        {
            var normalized = header.Replace('_', '-');
            if (normalized.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("X-Fanasa-", StringComparison.OrdinalIgnoreCase))
            {
                if (!header.Equals(normalized, StringComparison.Ordinal)
                    || normalized.StartsWith("X-Fanasa-", StringComparison.OrdinalIgnoreCase)
                    && !normalized.Equals("X-Fanasa-Context", StringComparison.OrdinalIgnoreCase)
                    && !normalized.Equals("X-Fanasa-Context-Signature", StringComparison.OrdinalIgnoreCase)) return null;
            }
        }
        var raw = Convert.FromBase64String(encoded);
        if (raw.Length > 16384 || Convert.ToBase64String(raw) != encoded) return null;
        using var document = JsonDocument.Parse(Utf8.GetString(raw), new JsonDocumentOptions { MaxDepth = 8 });
        var envelope = document.RootElement;
        if (!OnlyKeys(envelope, EnvelopeKeys) || !Integer(envelope.GetProperty("version"), 1, 1, out _)
            || !Text(envelope, "keyId", out var keyId) || !Text(envelope, "tier", out var tier)
            || !Seconds(envelope.GetProperty("issuedAt"), out var issued) || !Seconds(envelope.GetProperty("expiresAt"), out var expires)) return null;
        var keys = options.Keys.Where(key => key.KeyId == keyId && key.Tier == tier).ToArray();
        if (keys.Length != 1 || !await CertificateMatchesAsync(http, keys[0].ClientCertificateSha256!, time)) return null;
        var bodyHash = Hash(body);
        var parts = new[] { "fanasa.context.v1", http.Request.Method, target, Hash(Utf8.GetBytes(authorization)),
            bodyHash, Hash(Utf8.GetBytes(idempotency)), encoded };
        using var frame = new MemoryStream();
        foreach (var part in parts)
        {
            var bytes = Utf8.GetBytes(part);
            frame.Write(Utf8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            frame.Write(bytes);
        }
        var expected = HMACSHA256.HashData(Utf8.GetBytes(keys[0].Secret!), frame.ToArray());
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature))) return null;
        var now = Epoch(time.GetUtcNow());
        if (issued > now || expires <= issued || expires <= now || expires - issued > 30) return null;
        var context = ReadContext(envelope.GetProperty("context"), now);
        if (context is null || expires > context.DecisionValidUntil || expires > context.TokenExpiresAt) return null;
        if (!Guid.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var tenant) || tenant == Guid.Empty
            || !Guid.TryParseExact(context.ConsumerTenantId, "D", out var consumer) || tenant != consumer) return null;
        using var commandDocument = JsonDocument.Parse(Utf8.GetString(body), new JsonDocumentOptions { MaxDepth = 8 });
        if (commandDocument.RootElement.ValueKind != JsonValueKind.Object) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in commandDocument.RootElement.EnumerateObject())
            if (!CommandKeys.Contains(property.Name) || !names.Add(property.Name)) return null;
        var command = commandDocument.RootElement.Deserialize<OrgCommand>(JsonOptions);
        if (command is null || command.Operation != "unit.save"
            || !Guid.TryParseExact(command.RequestId, "D", out var request) || request == Guid.Empty) return null;
        return new(keyId, tier, keys[0].ClientCertificateSha256!.ToLowerInvariant(), issued, expires,
            http.Request.Method, target, authorization, idempotency, bodyHash, command, context);
    }

    private static GatewayAuthorizedContext? ReadContext(JsonElement context, decimal now)
    {
        if (!OnlyKeys(context, ContextKeys) || context.GetProperty("allowed").ValueKind != JsonValueKind.True
            || context.GetProperty("confirmed").ValueKind != JsonValueKind.True) return null;
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ContextKeys.Except(["contractVersion", "configRevision", "periodStartMs", "periodEndMs",
            "decisionValidUntil", "tokenExpiresAt", "allowed", "confirmed", "resourceTenant"]))
        {
            if (!Text(context, name, out var value) || !Identifier(value, 13000)) return null;
            strings.Add(name, value);
        }
        var nonce = strings["requestId"];
        if (nonce.Length != 34 || !nonce.StartsWith("g-", StringComparison.Ordinal) || !Hex(nonce[2..], 32, lowercase: true)
            || strings["decisionId"] != nonce || strings["admissionId"] != "admission:" + nonce
            || !Guid.TryParseExact(strings["subjectId"], "D", out var subject) || subject == Guid.Empty
            || !Guid.TryParseExact(strings["consumerTenantId"], "D", out var consumer) || consumer == Guid.Empty
            || !Guid.TryParseExact(strings["ownerTenantId"], "D", out var owner) || owner == Guid.Empty
            || !Slug(strings["productId"]) || !Slug(strings["environment"]) || !Slug(strings["action"])
            || !Slug(strings["contractId"]) || !Slug(strings["allocationId"])
            || !Integer(context.GetProperty("contractVersion"), 1, int.MaxValue, out var contractVersion)
            || !Integer(context.GetProperty("configRevision"), 1, MaximumSafeInteger, out var revision)
            || !Integer(context.GetProperty("periodStartMs"), 0, MaximumSafeInteger, out var starts)
            || !Integer(context.GetProperty("periodEndMs"), 1, MaximumSafeInteger, out var ends)
            || !Integer(context.GetProperty("tokenExpiresAt"), 1, (long)MaximumEpochSeconds, out var tokenExpires)
            || !Seconds(context.GetProperty("decisionValidUntil"), out var decisionExpires)
            || now >= decisionExpires || now >= tokenExpires || now * 1000 < starts || now * 1000 >= ends
            || !ValidPeriod(strings["allocationPeriodId"], starts, ends)) return null;
        var policy = strings["policyVersion"].Split('@');
        if (policy.Length != 2 || !Slug(policy[0]) || !long.TryParse(policy[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out var policyVersion) || policyVersion is < 1 or > MaximumSafeInteger
            || policyVersion.ToString(CultureInfo.InvariantCulture) != policy[1]) return null;
        var resource = context.GetProperty("resourceTenant");
        if (!OnlyKeys(resource, ResourceKeys)) return null;
        foreach (var name in ResourceKeys)
            if (!Text(resource, name, out var value) || value != strings[name]) return null;
        return new(nonce, strings["decisionId"], strings["subjectId"], strings["clientId"], strings["consumerTenantId"],
            strings["ownerTenantId"], strings["productId"], strings["environment"], strings["action"], strings["policyVersion"],
            strings["contractId"], contractVersion, strings["allocationId"], strings["allocationScope"], revision,
            strings["allocationPeriodId"], starts, ends,
            new(strings["consumerTenantId"], strings["productId"], strings["environment"], strings["action"]),
            decisionExpires, tokenExpires, strings["admissionId"]);
    }

    internal static async ValueTask<bool> CertificateMatchesAsync(HttpContext http, string pin, TimeProvider time)
    {
        if (!HasDirectTls(http) || !Hex(pin, 64, lowercase: false)) return false;
        _ = await http.Connection.GetClientCertificateAsync(http.RequestAborted);
        return CertificateMatches(http, pin, time);
    }

    internal static bool CertificateMatches(HttpContext http, string pin, TimeProvider time)
    {
        if (!HasDirectTls(http) || !Hex(pin, 64, lowercase: false)) return false;
        // The filter/executor already retrieved the native connection certificate asynchronously before the Fabric transaction.
        var certificate = http.Connection.ClientCertificate;
        if (certificate is null) return false;
        var now = time.GetUtcNow();
        if (now < new DateTimeOffset(certificate.NotBefore.ToUniversalTime())
            || now >= new DateTimeOffset(certificate.NotAfter.ToUniversalTime())) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(pin));
    }

    internal static bool HasDirectTls(HttpContext http)
    {
        // Kestrel supplies handshake and certificate through the same native TLS feature.
        // Certificate-forwarding middleware replaces the certificate feature; that proxy assertion is not peer authority here.
        var handshake = http.Features.Get<ITlsHandshakeFeature>();
        return http.Request.IsHttps && handshake is not null
            && ReferenceEquals(handshake, http.Features.Get<ITlsConnectionFeature>());
    }

    internal static bool ReadHeader(HttpContext http, string name, bool required, out string value)
    {
        value = string.Empty;
        if (!http.Request.Headers.TryGetValue(name, out var values)) return !required;
        if (values.Count != 1 || values[0] is not { } single || single.Length > 21848
            || single.Any(character => character < 32 || character == 127)) return false;
        value = single;
        return !required || value.Length > 0;
    }

    internal static bool OnlyKeys(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!keys.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) return false;
        return keys.Count == expected.Length;
    }

    internal static bool Identifier(string? value, int maximumBytes)
    {
        try
        {
            return value is { Length: > 0 } && value.Length <= maximumBytes
                && !value.Any(character => char.IsWhiteSpace(character) || character < 32 || character == 127)
                && Utf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException) { return false; }
    }
    internal static bool Hex(string? value, int length, bool lowercase) => value?.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'
            || !lowercase && character is >= 'A' and <= 'F');
    internal static bool ValidSecret(string? secret)
    {
        try { return secret is { Length: > 0 and <= 4096 } && Utf8.GetByteCount(secret) is >= 32 and <= 4096; }
        catch (EncoderFallbackException) { return false; }
    }
    private static bool ValidAuthorization(string authorization)
    {
        if (authorization.Length < 8 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var offset = 7;
        while (offset < authorization.Length && authorization[offset] == ' ') offset++;
        return offset < authorization.Length && !authorization[offset..].Any(character => char.IsWhiteSpace(character) || character == ',');
    }
    private static bool ValidIdempotency(string value) => value.Length <= 128
        && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '.' or '_' or ':' or '-');
    private static bool Slug(string value) => value.Length is > 0 and <= 64 && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static bool Text(JsonElement value, string name, out string text)
    {
        text = string.Empty;
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } result) return false;
        text = result;
        return true;
    }
    private static bool Integer(JsonElement value, long minimum, long maximum, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number)
            && number >= minimum && number <= maximum && value.GetRawText() == number.ToString(CultureInfo.InvariantCulture);
    }
    private static bool Seconds(JsonElement value, out decimal number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out number)
            && number >= 0 && number <= MaximumEpochSeconds;
    }
    private static bool ValidPeriod(string id, long starts, long ends)
    {
        if (!DateTimeOffset.TryParseExact(id + "-01T00:00:00Z", "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
            || start.Year < 1970 || start.Year == 9999 && start.Month == 12
            || id != start.ToString("yyyy-MM", CultureInfo.InvariantCulture)) return false;
        return start.ToUnixTimeMilliseconds() == starts && start.AddMonths(1).ToUnixTimeMilliseconds() == ends;
    }
    internal static decimal Epoch(DateTimeOffset instant) => (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10000000m;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
