# Independent contract and PIP adapters — API 0.2.0

Current package `0.3.0` adds the implemented PCC host integration described in [PCC-INTEGRATION.md](PCC-INTEGRATION.md). This document preserves the standalone adapter API; the standalone library itself still has no host/database dependency. Host JWT wiring and the authoritative reader are now implemented separately in `pcc/`, with default unavailable Gateway execution.

These adapters extend the existing native module. They run without an HTTP host, database, network client or additional package. They do not alter PCC authentication schemes, Program.cs, registry, persistence, migrations or contract business models. Schemas/profiles remain `1.0.0`/`fanasa.contract-policy/1` and `2.0.0`/`fanasa.contract-policy/2`.

## Producer boundary

[ContractEventAdapter.cs](src/Fanasa.ContractPolicy/ContractEventAdapter.cs) accepts a **normalized complete projection event**, the prior accepted state and a host-validated producer `ClaimsPrincipal`. Its producer binding comes from approved server configuration/registration; there is no default client or permission:

```csharp
var producerBinding = new ContractProducerBinding(
    issuerFromBearerConfiguration,
    audienceFromBearerConfiguration,
    registeredContractsProducerClientId,
    registeredContractsProducerWriteScope);
var events = new ContractEventAdapter(module.Projector, producerBinding);
ProjectionResult result = events.Handle(acceptedState, normalizedEnvelopeJson,
    principalFromSuccessfulBearerAuthentication);
```

The issuer must be HTTPS with the `/realms/fanasa` path and no userinfo/query/fragment. Exactly one authenticated identity is selected. Raw, case-sensitive `iss` and `azp` must each occur once and match the binding; its `aud` claims must contain the expected audience. Any conflicting `client_id` rejects the request. One `scope` claim must contain the configured scope as an exact ASCII-space-delimited token. Values are compared ordinally.

Unauthenticated identities may carry Fabric tenant claims but cannot supply these producer claims. Two authenticated identities are ambiguous and rejected. This follows the actual [Fabric claims transformation](../../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/TenantAuthorization.cs), which adds an unauthenticated identity. The existing [PCC authentication configuration](../../../../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Program.cs) keeps raw claim names with `MapInboundClaims=false`. Its developer-client `platform.registry` policy is not authorization for the contracts producer.

The host must use its existing **Bearer authentication result**, with signature, issuer, audience and lifetime validation. A browser cookie, decoded JWT, body-provided identity or locally asserted `IsAuthenticated` is not an alternative credential. No JWT decoding, signature validation or authentication scheme is implemented in this adapter; it enforces producer authorization after successful host authentication.

Authorization happens before parsing. JSON uses the strict module parser, with a 65,536-character transport bound plus depth/node limits. The adapter passes the authorized producer name `fanasa-contracts` to the projector, which independently requires the envelope producer to match. A body containing that name alone cannot authorize an event. Malformed/missing/unknown fields and unsupported snapshots are rejected.

Replay, equal-ID/equal-version conflicts, stale snapshots, suspension and stable allocation rules are handled by the existing projector. Both accepted and rejected results own independent state graphs. The host commits returned projection and receipts together under concurrency control; this adapter performs no writes or transaction. Producer authorization does not perform the business event-to-projection mapping or establish offer/subscription ownership. Those remain explicit input guarantees from the contracts owner and authoritative registry reader.

## Trusted PIP boundary

[TrustedAccessSnapshotProvider.cs](src/Fanasa.ContractPolicy/TrustedAccessSnapshotProvider.cs) supplies the following independent seam:

```csharp
IAccessAttributeSnapshotSource authoritativeReader = hostSnapshotReader;
var pip = new TrustedAccessSnapshotProvider(authoritativeReader,
    module.Projections, TimeProvider.System, TimeSpan.FromSeconds(30));

var query = new TrustedAccessSnapshotQuery(serverRequestId,
    subjectFromValidatedTokenAndClientPolicy,
    resourceFromRegisteredServerRoute,
    registeredAction, serverExecutionZone,
    authenticationValidUntilFromValidatedTokenAndClientPolicy);
TrustedAccessSnapshotResult snapshot = await pip.ReadAsync(query, cancellationToken);
if (!snapshot.Succeeded)
{
    // Host stops execution and maps snapshot.Status to its failure response.
    // There is no fallback to a previous Permit or client-supplied context.
    return;
}
PolicyDecision decision = module.Evaluator.Evaluate(snapshot.Projection, snapshot.Context);
```

The host supplies the authenticated subject/client/scopes, canonical UTC authentication expiry and route binding; the query does not take caller time, membership, grant, contract versions or limits. This typed API is a server boundary, not a request DTO to bind from a browser or Gateway body. `AuthenticationValid`/`ClientAuthorized` and the required `AuthenticationValidUntil` come from actual token and client-policy checks. There is no default infinite identity lifetime.

The injected source reads a correlated `AccessAttributeSnapshotData` containing accepted projection, tenant, membership, explicit grant, evidence and `ObservedAt`. It owns source authentication and consistent versioned reads across registry/tenancy/policy. The module does not supply a fake persistent reader or translate the legacy entitlement DTO into absent contract fields.

The provider checks projection semantics and profile schema; subject, consumer tenant and product must correlate across every row and the query. Evidence contract/policy versions must equal the accepted projection. The context uses server time sampled **after** the awaited read. Evidence expiry is the minimum of authentication expiry, source expiry, `ObservedAt + min(configured maximum age, profile TTL)` and the server profile horizon. Authentication expired during the read yields no usable context. Profile TTL remains 30 seconds for v2 and 60 seconds for v1; timestamps emitted to the evaluator are canonical UTC milliseconds. Freshness comparisons use full clock precision, so millisecond formatting does not misclassify a valid submillisecond observation.

Future-dated/expired/missing data, failed reads, invalid query/schema and correlation/version mismatch return a status and fixed errors with no usable projection/context. Caller cancellation propagates. A reader timeout/cancellation unrelated to the caller fails closed. There is no cache or stale Permit fallback. Inactive tenant/membership/grant snapshots remain correlated data and reach the PDP, which returns Deny through its normal gates. Buying a contract still creates no implicit grant or administrator authority.

The query passed to the reader is separate from the retained token/route graph; returned data and emitted context are independent copies. Caller mutation during initial serialization is unsupported. Parallel use also requires a concurrency-safe injected reader. No claim of multi-writer database or distributed invalidation correctness follows from these in-process adapters.

## Wiring proposal and acceptance boundary

Continue using the existing PCC module and project reference from [README.md](README.md). The immutable event adapter can be registered beside the singleton projector with a dedicated registered producer binding. Resolve the PIP provider according to the actual reader lifetime; a scoped Fabric reader must not be captured by a singleton. Obtain the event principal through the existing Bearer scheme/policy rather than the default cookie principal.

Operational activation still requires real registered producer/client/scope and approved commercial mapping. Package `0.3.0` implements the host token pipeline, accepted-state reader, coherent source and atomic state/receipt commits; see the current integration guide. PEP still requires both obligations with request-bound trusted outcomes before running an operation. The standalone adapters themselves create no Gateway, counters, seat admission, budget, ledger or live grants.

Adapter tests use synthetic principals and injected fixture readers. They establish authorization/correlation/freshness behavior and replay invariants, not cryptographic JWT validation, a live Keycloak login or a real registry/database integration. The current verification and source manifest are [EVIDENCE.v0.2.0.md](EVIDENCE.v0.2.0.md) and [manifest.v0.2.0.json](manifest.v0.2.0.json).

