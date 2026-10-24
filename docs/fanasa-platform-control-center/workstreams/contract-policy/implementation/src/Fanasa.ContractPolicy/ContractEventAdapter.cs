using System.Security.Claims;
using System.Text.Json;

namespace Fanasa.ContractPolicy;

/// <summary>Dedicated producer authorization configuration supplied by the host registry.</summary>
public sealed record ContractProducerBinding(string Issuer, string Audience, string ClientId,
    string RequiredScope);

/// <summary>
/// Accepts normalized complete snapshots from an already validated bearer principal.
/// The host verifies token signature, issuer, audience and lifetime before this adapter.
/// No cookie fallback, token decoding, authentication scheme or persistence is provided.
/// </summary>
public sealed class ContractEventAdapter
{
    public const int MaximumPayloadCharacters = 65_536;
    public const string AuthorizedProducer = "fanasa-contracts";
    private readonly IContractProjectionProjector _projector;
    private readonly ContractProducerBinding _binding;

    public ContractEventAdapter(IContractProjectionProjector projector, ContractProducerBinding binding)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        ArgumentNullException.ThrowIfNull(binding);
        static bool Bounded(string? value) => value is { Length: > 0 and <= 1024 }
            && value == value.Trim() && !value.Any(char.IsControl);
        if (!Bounded(binding.Issuer) || !Uri.TryCreate(binding.Issuer, UriKind.Absolute, out var issuer)
            || issuer.Scheme != Uri.UriSchemeHttps || issuer.UserInfo.Length != 0
            || issuer.Query.Length != 0 || issuer.Fragment.Length != 0
            || issuer.AbsolutePath != "/realms/fanasa"
            || !Bounded(binding.Audience) || !Bounded(binding.ClientId)
            || !Bounded(binding.RequiredScope)
            || binding.RequiredScope.Any(c => c < '!' || c > '~' || c is '"' or '\\'))
            throw new ArgumentException("A dedicated issuer/audience/client/scope binding is required.", nameof(binding));
        _binding = binding with { };
    }

    public ProjectionResult Handle(ProjectionState? state, string? payload, ClaimsPrincipal? principal)
    {
        ProjectionResult Rejected(string error) => new("rejected",
            state is null ? null : ContractJson.Copy(state), Array.AsReadOnly(new[] { error }));

        // Producer names in the body are not credentials. Authentication precedes parsing.
        if (!IsAuthorized(principal)) return Rejected("producer-not-authorized");
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaximumPayloadCharacters)
            return Rejected("invalid-event-json");

        ContractProjectionEvent? envelope;
        try
        {
            envelope = ContractJson.Parse(payload)?.Deserialize<ContractProjectionEvent>(ContractJson.Options);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or
                                      InvalidOperationException or NotSupportedException or
                                      FormatException or OverflowException)
        {
            return Rejected("invalid-event-json");
        }
        // The projector checks the envelope's producer against this verified binding,
        // then schema, version, identity, replay and configuration invariants.
        return _projector.Apply(state, envelope, AuthorizedProducer);
    }

    private bool IsAuthorized(ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        var identities = principal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1) return false;
        var identity = identities[0];
        static string? Single(ClaimsIdentity identity, string claim)
        {
            var matches = identity.Claims.Where(x => string.Equals(x.Type, claim, StringComparison.Ordinal))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0].Value : null;
        }
        if (Single(identity, "iss") != _binding.Issuer || Single(identity, "azp") != _binding.ClientId)
            return false;
        if (!identity.Claims.Any(x => x.Type == "aud" && x.Value == _binding.Audience)
            || identity.Claims.Any(x => x.Type == "client_id" && x.Value != _binding.ClientId))
            return false;
        var scope = Single(identity, "scope");
        return scope is not null && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(_binding.RequiredScope, StringComparer.Ordinal);
    }
}
