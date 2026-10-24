using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;

// All readers and principals below are synthetic. These tests exercise the host
// boundaries; they do not verify a JWT signature or read a live membership store.
internal static class AdapterTests
{
    public static void Run(Action<string, Action> test, string schemaDirectory, string fixtureDirectory)
    {
        var schemas = new ContractSchemaValidator(schemaDirectory);
        var validator = new ContractProjectionValidator(schemas);
        var evaluator = new ContractPolicyEvaluator(validator);
        var projector = new ContractProjectionProjector(validator);
        var projection = Read<ContractAccessProjection>(Fixture("integration-projection.v2.json"));
        var context = Read<AccessEvaluationContext>(Fixture("integration-context.v2.json"));
        var projectionEvent = Read<ContractProjectionEvent>(Fixture("integration-projection-event.v2.json"));
        var expectedDecision = Fixture("integration-decision.v2.json");
        var now = DateTimeOffset.Parse(context.Environment.Now);
        var query = new TrustedAccessSnapshotQuery(context.RequestId, context.Subject, context.Resource, context.Action, context.Environment.Zone, context.Evidence.ValidUntil);
        var data = new AccessAttributeSnapshotData(projection, context.Tenant, context.Membership, context.Grant, context.Evidence, now);
        var maximumAge = TimeSpan.FromMinutes(2);
        var binding = new ContractProducerBinding("https://fixture.identity.invalid/realms/fanasa", "fixture-contract-projection-api", "fixture-contracts-producer", "contracts.projection.write");
        var adapter = new ContractEventAdapter(projector, binding);
        var payload = Json(projectionEvent);

        Test("PIP synthetic reader produces the complete integration golden decision", () =>
        {
            var source = Synthetic(data);
            var result = Get(new TrustedAccessSnapshotProvider(source, validator, new FixtureClock(now), maximumAge), query);
            Ready(result); Equal(1, source.Calls);
            EqualJson(expectedDecision, ContractJson.ToNode(evaluator.Evaluate(result.Projection, result.Context)));
            EqualJson(ContractJson.ToNode(projection), ContractJson.ToNode(result.Projection));
            Equal(context.Environment.Now, result.Context!.Environment.Now);
            Equal("2026-10-10T06:30:29.000Z", result.Context.Evidence.ValidUntil);
        });

        Test("PIP server clock supplies evaluation time and cannot extend observed freshness", () =>
        {
            var result = Get(Provider(data, now.AddSeconds(5)), query); Ready(result);
            Equal("2026-10-10T06:30:04.000Z", result.Context!.Environment.Now);
            Equal("2026-10-10T06:30:29.000Z", result.Context.Evidence.ValidUntil);
            Equal("Permit", evaluator.Evaluate(result.Projection, result.Context).Decision);
        });
        Test("PIP formats server time as canonical UTC", () =>
        {
            var result = Get(Provider(data, now.ToOffset(TimeSpan.FromHours(3.5))), query); Ready(result);
            Equal(context.Environment.Now, result.Context!.Environment.Now);
        });
        Test("PIP retains full clock precision before formatting a preceding observation", () =>
        {
            var result = Get(Provider(data with { ObservedAt = now.AddTicks(1) }, now.AddTicks(2)), query);
            Ready(result); Equal(context.Environment.Now, result.Context!.Environment.Now);
        });
        Test("PIP rejects an observation even one tick in the future", () =>
            Failure(Get(Provider(data with { ObservedAt = now.AddTicks(1) }), query), "stale-snapshot"));
        Test("PIP rejects age expiry using full precision", () =>
            Failure(Get(Provider(data, now.AddSeconds(30).AddTicks(1)), query), "stale-snapshot"));
        Test("PIP configured maximum age can shorten the profile ceiling", () =>
        {
            var result = Get(new TrustedAccessSnapshotProvider(Synthetic(data), validator, new FixtureClock(now), TimeSpan.FromSeconds(5)), query);
            Ready(result); Equal("2026-10-10T06:30:04.000Z", result.Context!.Evidence.ValidUntil);
        });
        Test("PIP source evidence can shorten both freshness bounds", () =>
        {
            var shortData = data with { Evidence = data.Evidence with { ValidUntil = "2026-10-10T06:30:00.500Z" } };
            var result = Get(Provider(shortData), query); Ready(result);
            Equal("2026-10-10T06:30:00.500Z", result.Context!.Evidence.ValidUntil);
        });
        foreach (var expiry in new[] { "2026-10-10T06:29:58.999Z", context.Environment.Now })
            Test($"PIP rejects expired authentication: {expiry}", () =>
                Failure(Get(Provider(data), query with { AuthenticationValidUntil = expiry }), "stale-snapshot"));
        Test("PIP authentication expiry bounds evidence, PDP Permit and PEP consumption", () =>
        {
            var result = Get(Provider(data), query with { AuthenticationValidUntil = "2026-10-10T06:30:09.000Z" }); Ready(result);
            Equal("2026-10-10T06:30:09.000Z", result.Context!.Evidence.ValidUntil);
            var decision = evaluator.Evaluate(result.Projection, result.Context);
            Equal("Permit", decision.Decision); Equal("2026-10-10T06:30:09.000Z", decision.ValidUntil);
            var pep = new ContractPolicyPep(schemas);
            string[] fulfilled = ["urn:fanasa:obligation:admit-allocation:1", "urn:fanasa:obligation:resource-tenant:1"];
            True(pep.CanExecute(decision, fulfilled, "2026-10-10T06:30:08.999Z"));
            True(!pep.CanExecute(decision, fulfilled, "2026-10-10T06:30:09.000Z"));
        });
        Test("PIP source cannot extend the retained authentication deadline", () =>
        {
            var original = query with { AuthenticationValidUntil = "2026-10-10T06:30:09.000Z" };
            var before = Json(original);
            var source = new SyntheticSource((received, _) =>
            {
                received = received with { AuthenticationValidUntil = "2026-11-01T00:00:00.000Z" };
                return ValueTask.FromResult<AccessAttributeSnapshotData?>(data with { Evidence = data.Evidence with { ValidUntil = received.AuthenticationValidUntil } });
            });
            var result = Get(new TrustedAccessSnapshotProvider(source, validator, new FixtureClock(now), maximumAge), original); Ready(result);
            Equal("2026-10-10T06:30:09.000Z", result.Context!.Evidence.ValidUntil); Equal(before, Json(original));
        });
        Test("PIP rechecks authentication expiry after awaited source lookup", () =>
        {
            var clock = new FixtureClock(now);
            var source = new SyntheticSource(async (_, _) =>
            {
                await Task.Yield(); clock.Set(now.AddSeconds(6));
                return data;
            });
            var provider = new TrustedAccessSnapshotProvider(source, validator, clock, maximumAge);
            Failure(Get(provider, query with { AuthenticationValidUntil = "2026-10-10T06:30:04.000Z" }), "stale-snapshot");
        });
        Test("PIP compares authentication expiry with full submillisecond server time", () =>
        {
            var q = query with { AuthenticationValidUntil = "2026-10-10T06:29:59.001Z" };
            var result = Get(Provider(data, now.AddTicks(9_999)), q); Ready(result);
            Equal("2026-10-10T06:29:59.001Z", result.Context!.Evidence.ValidUntil);
            Failure(Get(Provider(data, now.AddTicks(10_000)), q), "stale-snapshot");
            Failure(Get(Provider(data, now.AddTicks(10_001)), q), "stale-snapshot");
        });

        foreach (var (name, snapshot) in new[]
        {
            ("too old", data with { ObservedAt = now.AddMilliseconds(-30_001) }),
            ("at profile age boundary", data with { ObservedAt = now.AddSeconds(-30) }),
            ("observed in the future", data with { ObservedAt = now.AddMilliseconds(1) }),
            ("source evidence already expired", data with { Evidence = data.Evidence with { ValidUntil = "2026-10-10T06:29:58.999Z" } }),
            ("source evidence expires at server now", data with { Evidence = data.Evidence with { ValidUntil = context.Environment.Now } })
        })
            Test($"PIP rejects stale snapshot: {name}", () => Failure(Get(Provider(snapshot), query), "stale-snapshot"));

        Test("PIP expires a reused source snapshot instead of falling back to earlier Permit", () =>
        {
            var clock = new FixtureClock(now);
            var provider = new TrustedAccessSnapshotProvider(Synthetic(data), validator, clock, maximumAge);
            Ready(Get(provider, query)); clock.Set(now.AddSeconds(30));
            Failure(Get(provider, query), "stale-snapshot");
        });
        Test("PIP returns no context when the synthetic reader has no snapshot", () => Failure(Get(Provider(null), query), "missing-snapshot"));
        Test("PIP source exception fails closed", () =>
        {
            var source = new SyntheticSource((_, _) => throw new IOException("synthetic source failure"));
            var result = Get(new TrustedAccessSnapshotProvider(source, validator, new FixtureClock(now), maximumAge), query);
            Failure(result, "source-unavailable");
            True(result.Errors.All(error => !error.Contains("synthetic source failure", StringComparison.Ordinal)));
        });

        foreach (var (name, snapshot) in new[]
        {
            ("contract evidence version", data with { Evidence = data.Evidence with { ContractVersion = 8 } }),
            ("policy evidence version", data with { Evidence = data.Evidence with { PolicyVersion = 4 } }),
            ("tenant consumer", data with { Tenant = data.Tenant with { TenantId = projection.OwnerTenantId } }),
            ("membership subject", data with { Membership = data.Membership with { SubjectId = projection.OwnerTenantId } }),
            ("membership consumer", data with { Membership = data.Membership with { TenantId = projection.OwnerTenantId } }),
            ("grant subject", data with { Grant = data.Grant with { SubjectId = projection.OwnerTenantId } }),
            ("grant consumer", data with { Grant = data.Grant with { ConsumerTenantId = projection.OwnerTenantId } }),
            ("grant product", data with { Grant = data.Grant with { ProductId = "club" } })
        })
            Test($"PIP correlates all authority rows: {name}", () => Failure(Get(Provider(snapshot), query), "correlation-mismatch"));
        Test("PIP correlates the requested consumer with the returned projection", () =>
            Failure(Get(Provider(data), query with { Resource = query.Resource with { ConsumerTenantId = projection.OwnerTenantId } }), "correlation-mismatch"));
        Test("PIP correlates the requested product with the returned projection", () =>
            Failure(Get(Provider(data), query with { Resource = query.Resource with { ProductId = "club" } }), "correlation-mismatch"));
        Test("PIP rejects an invalid source projection", () =>
            Failure(Get(Provider(data with { Projection = projection with { EligibleRoles = ["PlatformAdmin"] } }), query), "invalid-snapshot"));
        Test("PIP rejects missing authority rows", () =>
            Failure(Get(Provider(data with { Membership = null! }), query), "missing-snapshot"));
        Test("PIP rejects an oversized authority graph without throwing", () =>
        {
            var oversized = data with { Grant = data.Grant with { Actions = Enumerable.Range(0, 10_001).Select(i => $"action-{i}").ToArray() } };
            Failure(Get(Provider(oversized), query), "invalid-snapshot");
        });

        foreach (var (name, invalidQuery) in new (string, TrustedAccessSnapshotQuery?)[]
        {
            ("missing query", null),
            ("authentication false", query with { AuthenticatedSubject = query.AuthenticatedSubject with { AuthenticationValid = false } }),
            ("client authorization false", query with { AuthenticatedSubject = query.AuthenticatedSubject with { ClientAuthorized = false } }),
            ("invalid request identifier", query with { RequestId = "client;admin=true" }),
            ("invalid zone identifier", query with { Zone = "ir-private-1\n" }),
            ("missing authenticated subject", query with { AuthenticatedSubject = null! }),
            ("missing authentication expiry", query with { AuthenticationValidUntil = null! }),
            ("malformed authentication expiry", query with { AuthenticationValidUntil = "tomorrow" }),
            ("noncanonical authentication expiry", query with { AuthenticationValidUntil = "2026-10-10T06:30:59Z" }),
            ("impossible authentication expiry date", query with { AuthenticationValidUntil = "2026-02-30T06:30:59.000Z" })
        })
            Test($"PIP rejects invalid trusted query before source lookup: {name}", () =>
            {
                var source = Synthetic(data);
                Failure(Get(new TrustedAccessSnapshotProvider(source, validator, new FixtureClock(now), maximumAge), invalidQuery), "invalid-query");
                Equal(0, source.Calls);
            });

        foreach (var (name, snapshot) in new[]
        {
            ("inactive tenant", data with { Tenant = data.Tenant with { Active = false } }),
            ("inactive membership", data with { Membership = data.Membership with { Active = false } }),
            ("inactive grant", data with { Grant = data.Grant with { Active = false } }),
            ("uneligible administrator grant", data with { Grant = data.Grant with { Role = "PlatformAdmin" } }),
            ("empty explicit action grant", data with { Grant = data.Grant with { Actions = [] } })
        })
            Test($"PIP preserves negative authority for PDP: {name}", () =>
            {
                var result = Get(Provider(snapshot), query); Ready(result);
                var decision = evaluator.Evaluate(result.Projection, result.Context);
                Equal("Deny", decision.Decision); Equal(0, decision.Obligations.Count);
            });
        Test("PIP preserves wrong audience for the PDP identity gate", () =>
        {
            var q = query with { AuthenticatedSubject = query.AuthenticatedSubject with { Audience = "fanasa-club-api" } };
            var result = Get(Provider(data), q); Ready(result);
            Equal("Deny", evaluator.Evaluate(result.Projection, result.Context).Decision);
        });
        Test("PIP cannot fabricate a platform administrator role from product eligibility", () =>
        {
            var result = Get(Provider(data), query); Ready(result);
            Equal("ApiInvoker", result.Context!.Grant.Role);
            EqualJson(ContractJson.ToNode(data.Grant), ContractJson.ToNode(result.Context.Grant));
            True(!result.Projection!.EligibleRoles.Contains("PlatformAdmin", StringComparer.Ordinal));
        });

        Test("PIP propagates caller cancellation", () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var provider = Provider(data);
            ThrowsCancellation(() => Get(provider, query, cancellation.Token), cancellation.Token);
        });
        Test("PIP propagates cancellation during source lookup", () =>
        {
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new SyntheticSource(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return data;
            });
            var provider = new TrustedAccessSnapshotProvider(source, validator, new FixtureClock(now), maximumAge);
            var pending = provider.ReadAsync(query, cancellation.Token).AsTask();
            True(entered.Task.Wait(TimeSpan.FromSeconds(2))); cancellation.Cancel();
            ThrowsCancellation(() => pending.GetAwaiter().GetResult(), cancellation.Token);
        });
        Test("PIP source receives an independent query and returned graphs are independent", () =>
        {
            var originalQuery = ContractJson.Copy(query); var queryBefore = Json(originalQuery); var dataBefore = Json(data);
            var source = new SyntheticSource((received, _) =>
            {
                if (received.AuthenticatedSubject.Scopes is IList<string> scopes) scopes[0] = "source.changed";
                return ValueTask.FromResult<AccessAttributeSnapshotData?>(data);
            });
            var provider = new TrustedAccessSnapshotProvider(source, validator, new FixtureClock(now), maximumAge);
            var result = Get(provider, originalQuery); Ready(result);
            Equal(queryBefore, Json(originalQuery)); Equal(dataBefore, Json(data));
            Equal(context.Action, result.Context!.Subject.Scopes[0]);
            if (result.Context.Subject.Scopes is IList<string> scopes) scopes[0] = "caller.changed";
            if (result.Projection!.Limits.Scope.Environments is IList<string> environments) environments[0] = "production";
            Equal(queryBefore, Json(originalQuery)); Equal(dataBefore, Json(data));
            var next = Get(provider, originalQuery); Ready(next);
            EqualJson(expectedDecision, ContractJson.ToNode(evaluator.Evaluate(next.Projection, next.Context)));
        });

        Test("producer adapter accepts the complete envelope then recognizes replay", () =>
        {
            var first = adapter.Handle(null, payload, Principal()); Equal("applied", first.Status);
            EqualJson(ContractJson.ToNode(projection), ContractJson.ToNode(first.State!.Projection));
            Equal("duplicate", adapter.Handle(first.State, payload, Principal()).Status);
        });
        Test("producer adapter accepts an expected audience among multiple claims", () =>
        {
            var principal = Principal(extra: [new Claim("aud", "another-valid-api")]);
            Equal("applied", adapter.Handle(null, payload, principal).Status);
        });
        Test("producer adapter accepts a matching optional client_id", () =>
            Equal("applied", adapter.Handle(null, payload, Principal(extra: [new Claim("client_id", binding.ClientId)])).Status));
        Test("producer adapter permits unrelated unauthenticated Fabric identity", () =>
        {
            var principal = Principal(); principal.AddIdentity(new ClaimsIdentity([new Claim("tenant_id", projection.ConsumerTenantId), new Claim("role", "OrganizationAdmin")]));
            Equal("applied", adapter.Handle(null, payload, principal).Status);
        });

        foreach (var (name, principal) in new (string, ClaimsPrincipal?)[]
        {
            ("no principal", null),
            ("unauthenticated identity", Principal(authenticated: false)),
            ("wrong issuer", Principal(replace: new Claim("iss", "https://other.identity.invalid/realms/fanasa"))),
            ("wrong OAuth client", Principal(replace: new Claim("azp", "another-client"))),
            ("wrong audience", Principal(replace: new Claim("aud", "another-api"))),
            ("wrong scope", Principal(replace: new Claim("scope", "contracts.read"))),
            ("scope substring", Principal(replace: new Claim("scope", "x" + binding.RequiredScope + "x"))),
            ("scope tab delimiter", Principal(replace: new Claim("scope", binding.RequiredScope + "\tother"))),
            ("duplicate issuer", Principal(extra: [new Claim("iss", binding.Issuer)])),
            ("duplicate authorized party", Principal(extra: [new Claim("azp", binding.ClientId)])),
            ("duplicate scope", Principal(extra: [new Claim("scope", binding.RequiredScope)])),
            ("conflicting client_id", Principal(extra: [new Claim("client_id", "another-client")]))
        })
            Test($"producer adapter rejects untrusted credential binding: {name}", () =>
            {
                var state = adapter.Handle(null, payload, Principal()).State!; var before = Json(state);
                var result = adapter.Handle(state, payload, principal);
                Equal("rejected", result.Status); True(result.Errors.Count > 0);
                Equal(before, Json(result.State)); Equal(before, Json(state));
            });
        foreach (var claimType in new[] { "iss", "azp", "aud", "scope" })
            Test($"producer adapter cannot borrow {claimType} from unauthenticated identity", () =>
            {
                var valid = Principal(); var selected = valid.Identities.Single(); var removed = selected.FindFirst(claimType)!; selected.RemoveClaim(removed);
                valid.AddIdentity(new ClaimsIdentity([removed]));
                Equal("rejected", adapter.Handle(null, payload, valid).Status);
            });
        Test("producer adapter rejects two authenticated identities", () =>
        {
            var principal = Principal(); principal.AddIdentity(Principal().Identities.Single());
            Equal("rejected", adapter.Handle(null, payload, principal).Status);
        });
        foreach (var claimType in new[] { "iss", "azp", "aud", "scope" })
            Test($"producer adapter requires exact case for claim type {claimType}", () =>
            {
                var principal = Principal(); var identity = principal.Identities.Single();
                var original = identity.Claims.Single(c => c.Type == claimType); identity.RemoveClaim(original);
                identity.AddClaim(new Claim(claimType.ToUpperInvariant(), original.Value));
                Equal("rejected", adapter.Handle(null, payload, principal).Status);
            });
        Test("producer adapter rejects a principal with only uppercase security claim types", () =>
        {
            var claims = Principal().Identities.Single().Claims.Select(c => new Claim(c.Type.ToUpperInvariant(), c.Value));
            Equal("rejected", adapter.Handle(null, payload, new ClaimsPrincipal(new ClaimsIdentity(claims, "synthetic-bearer-test"))).Status);
        });
        Test("producer adapter cannot use uppercase SCOPE to supply missing producer permission", () =>
        {
            var principal = Principal(replace: new Claim("scope", "contracts.read"), extra: [new Claim("SCOPE", binding.RequiredScope)]);
            Equal("rejected", adapter.Handle(null, payload, principal).Status);
        });
        foreach (var invalidIssuer in new[]
        {
            "http://fixture.identity.invalid/realms/fanasa", "file:///realms/fanasa", "https://fixture.identity.invalid/realms/other",
            "https://fixture.identity.invalid/realms/fanasa?admin=true", "https://fixture.identity.invalid/realms/fanasa#fragment",
            "https://user@fixture.identity.invalid/realms/fanasa"
        })
            Test($"producer adapter constructor rejects untrusted issuer binding: {invalidIssuer}", () =>
                ThrowsArgument(() => new ContractEventAdapter(projector, binding with { Issuer = invalidIssuer })));
        Test("producer body cannot authorize an unauthenticated sender", () =>
            Equal("rejected", adapter.Handle(null, payload, new ClaimsPrincipal(new ClaimsIdentity([new Claim("producer", "fanasa-contracts")]))).Status));
        Test("authorized token cannot rename the producer in the body", () =>
        {
            var renamed = projectionEvent with { Producer = "untrusted-producer" };
            Equal("rejected", adapter.Handle(null, Json(renamed), Principal()).Status);
        });

        foreach (var (name, invalidPayload) in new (string, string?)[]
        {
            ("missing", null), ("empty", ""), ("whitespace", "   "), ("invalid JSON", "{"),
            ("null JSON", "null"), ("partial envelope", "{\"producer\":\"fanasa-contracts\"}"),
            ("duplicate version", payload.Insert(1, "\"schemaVersion\":\"2.0.0\",")),
            ("unknown envelope field", payload.Insert(1, "\"platformAdmin\":true,")),
            ("unsupported schema", Json(projectionEvent with { SchemaVersion = "99.0.0" })),
            ("over payload bound", payload + new string(' ', ContractEventAdapter.MaximumPayloadCharacters))
        })
            Test($"producer adapter rejects malformed bounded payload: {name}", () =>
            {
                var state = adapter.Handle(null, payload, Principal()).State!; var before = Json(state);
                var result = adapter.Handle(state, invalidPayload, Principal());
                Equal("rejected", result.Status); True(result.Errors.Count > 0);
                Equal(before, Json(result.State)); Equal(before, Json(state));
            });
        Test("producer adapter accepts payload exactly at character bound", () =>
        {
            var padded = payload + new string(' ', ContractEventAdapter.MaximumPayloadCharacters - payload.Length);
            Equal("applied", adapter.Handle(null, padded, Principal()).Status);
        });
        Test("producer adapter preserves conflicting event ID rejection", () =>
        {
            var state = adapter.Handle(null, payload, Principal()).State!;
            var conflict = projectionEvent with { Projection = projection with { Status = "Suspended" } };
            var result = adapter.Handle(state, Json(conflict), Principal()); Equal("conflict", result.Status);
            EqualJson(ContractJson.ToNode(state), ContractJson.ToNode(result.State));
        });
        Test("producer adapter applies newer suspension and remembers stale older allocation", () =>
        {
            var first = adapter.Handle(null, payload, Principal()).State!;
            var suspension = projectionEvent with
            {
                EventId = "fixture-adapter-event-8", AggregateVersion = 8,
                Projection = projection with { ContractVersion = 8, Status = "Suspended", Limits = projection.Limits with { ConfigRevision = 2, Rate = projection.Limits.Rate with { RequestsPerSecond = 6 } } }
            };
            var suspended = adapter.Handle(first, Json(suspension), Principal()); Equal("applied", suspended.Status);
            var older = projectionEvent with { EventId = "fixture-adapter-event-6", AggregateVersion = 6, Projection = projection with { ContractVersion = 6 } };
            var stale = adapter.Handle(suspended.State, Json(older), Principal()); Equal("stale", stale.Status);
            EqualJson(ContractJson.ToNode(suspended.State!.Projection), ContractJson.ToNode(stale.State!.Projection));
            Equal("duplicate", adapter.Handle(stale.State, Json(older), Principal()).Status);
            Equal("Suspended", stale.State.Projection.Status);
            Equal("Deny", evaluator.Evaluate(stale.State.Projection, context with { Evidence = context.Evidence with { ContractVersion = 8 } }).Decision);
        });

        JsonNode Fixture(string name) => ContractJson.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, name)))!;
        TrustedAccessSnapshotProvider Provider(AccessAttributeSnapshotData? snapshot, DateTimeOffset? serverNow = null) =>
            new(Synthetic(snapshot), validator, new FixtureClock(serverNow ?? now), maximumAge);
        ClaimsPrincipal Principal(Claim? replace = null, IEnumerable<Claim>? extra = null, bool authenticated = true)
        {
            var claims = new List<Claim> { new("iss", binding.Issuer), new("aud", binding.Audience), new("azp", binding.ClientId), new("scope", binding.RequiredScope + " contracts.read") };
            if (replace is not null) { claims.RemoveAll(c => c.Type == replace.Type); claims.Add(replace); }
            if (extra is not null) claims.AddRange(extra);
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "synthetic-bearer-test" : null));
        }
        void Test(string name, Action body)
            => test("adapter " + name, body);
    }

    private static SyntheticSource Synthetic(AccessAttributeSnapshotData? data) => new((_, cancellation) =>
    {
        cancellation.ThrowIfCancellationRequested();
        return ValueTask.FromResult(data);
    });
    private static TrustedAccessSnapshotResult Get(TrustedAccessSnapshotProvider provider, TrustedAccessSnapshotQuery? query, CancellationToken cancellation = default) =>
        provider.ReadAsync(query, cancellation).GetAwaiter().GetResult();
    private static T Read<T>(JsonNode node) => node.Deserialize<T>(ContractJson.Options)!;
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static void Ready(TrustedAccessSnapshotResult result)
    {
        Equal("ready", result.Status); True(result.Succeeded); True(result.Projection is not null); True(result.Context is not null); Equal(0, result.Errors.Count);
    }
    private static void Failure(TrustedAccessSnapshotResult result, string status)
    {
        Equal(status, result.Status); True(!result.Succeeded); True(result.Projection is null); True(result.Context is null); True(result.Errors.Count > 0);
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}");
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true"); }
    private static void EqualJson(JsonNode? expected, JsonNode? actual)
    {
        if (!JsonNode.DeepEquals(expected, actual)) throw new InvalidOperationException($"JSON differs. Expected {expected?.ToJsonString()}; actual {actual?.ToJsonString()}");
    }
    private static void ThrowsCancellation(Action action, CancellationToken token)
    {
        try { action(); }
        catch (OperationCanceledException error) when (error.CancellationToken == token) { return; }
        throw new InvalidOperationException("Expected cancellation with the caller token");
    }
    private static void ThrowsArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected invalid binding to throw ArgumentException");
    }
    private sealed class FixtureClock(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;
        public override DateTimeOffset GetUtcNow() => _current;
        public void Set(DateTimeOffset current) => _current = current;
    }
    private sealed class SyntheticSource(Func<TrustedAccessSnapshotQuery, CancellationToken, ValueTask<AccessAttributeSnapshotData?>> read) : IAccessAttributeSnapshotSource
    {
        public int Calls { get; private set; }
        public ValueTask<AccessAttributeSnapshotData?> ReadAsync(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            return read(query, cancellationToken);
        }
    }
}
