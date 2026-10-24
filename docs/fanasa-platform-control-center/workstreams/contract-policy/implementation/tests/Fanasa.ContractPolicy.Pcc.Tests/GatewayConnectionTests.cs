using System.Text.Json;
using System.Net;
using Fanasa.AccessManagement.Web.ContractPolicies;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.ContractPolicy;

// All publications and observations in this test are synthetic local evidence.
internal static class GatewayConnectionTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Gateway limits hashes match the original NeoContract dispatcher implementation", () =>
        {
            // Expected values were executed through the actual serviceLimitHash export in
            // E:/SJVS/Projects/NeoContract/lib/service-limits.mjs, source SHA256
            // 93a78317aece1660bca3c273f2a1bae688cd955ffe23c7f0459b99360a153f62.
            // These literals intentionally do not reuse or mirror the C# canonicalizer.
            var document = ContractJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "integration-projection-event.v2.json")))!;
            Equal("c623a349e7b74a42aa78ea8c873bfd2dcefadb94b070fab082d46fa5a0225240", GatewayCanonicalJson.Hash(document["projection"]!["limits"]!));
            Equal("74bab4611dad4abe5f978723cda38cac2522ad67e942392d8ddd8ad9f2440cff", GatewayCanonicalJson.Hash(ContractJson.Parse("{\"requestsPerSecond\":0.0000001}")));
            Equal("2c60b4323a3b8dba2c65cf266a8b64e7b83498167adb748a453598af831329a1", GatewayCanonicalJson.Hash(ContractJson.Parse("{\"requestsPerSecond\":0.000001}")));
            Equal("15073d8b69029122113e72d6a2be168cd5f9b3e0ee9c7f759ca9906f8f14ef90", GatewayCanonicalJson.Hash(ContractJson.Parse("{\"z\":\"فن‌آسا\",\"a\":\"😀/é\"}")));
            return Task.CompletedTask;
        });
        await using var fixture = await PccFixture.Start();
        var initial = fixture.Event;
        await test("Gateway publication remains pending after projection apply", () =>
        {
            Check(fixture.Store.ReadGatewayPublication(fixture.ContractId, initial.EventId) is null, "Unapplied event has no publication");
            Equal("applied", Apply(fixture, initial).Status);
            var publication = Publication(fixture, initial.EventId);
            Equal(initial.EventId, publication.EventId); Equal(initial.AggregateVersion, publication.AggregateVersion);
            Equal(initial.Projection.ContractVersion, publication.Projection.ContractVersion);
            Equal(fixture.Store.Read(fixture.ContractId)!.Receipts.Single(x => x.EventId == initial.EventId).Fingerprint, publication.EventFingerprint);
            Check(publication.LimitsSha256.Length == 64, "Exact limits digest is frozen");
            Check(fixture.Store.ReadGatewayAcknowledgement(fixture.ContractId, initial.EventId) is null, "Projection apply is not an observed Gateway receipt");
            return Task.CompletedTask;
        });
        await test("Projection HTTP response remains 202 while Gateway observer is unavailable", async () =>
        {
            var before = Snapshot(fixture);
            using var response = await fixture.Ingest(initial, fixture.Token(producer: true));
            Equal(HttpStatusCode.Accepted, response.StatusCode);
            Check(fixture.Store.ReadGatewayAcknowledgement(fixture.ContractId, initial.EventId) is null,
                "A successful projection response cannot impersonate observed Gateway acceptance");
            Equal(before, Snapshot(fixture));
        });
        foreach (var mode in new[] { "contract-version", "allocation", "canonical-scope", "config-revision", "limits-hash", "before-effective", "future", "invalid-time" })
            await test("Gateway observation rejects " + mode + " without durable writes", () =>
            {
                var publication = Publication(fixture, initial.EventId); var observation = Observation(publication);
                observation = mode switch
                {
                    "contract-version" => observation with { ContractVersion = observation.ContractVersion + 1 },
                    "allocation" => observation with { AllocationId = "different-allocation" },
                    "canonical-scope" => observation with { CanonicalScope = observation.CanonicalScope + ":different" },
                    "config-revision" => observation with { ConfigRevision = observation.ConfigRevision + 1 },
                    "limits-hash" => observation with { LimitsSha256 = new string('0', 64) },
                    "before-effective" => observation with { ObservedAt = Utc(DateTimeOffset.Parse(publication.Projection.EffectiveAt).AddMilliseconds(-1)) },
                    "future" => observation with { ObservedAt = Utc(DateTimeOffset.UtcNow.AddMinutes(1)) },
                    "invalid-time" => observation with { ObservedAt = "not-a-timestamp" },
                    _ => throw new InvalidOperationException()
                };
                var before = Snapshot(fixture);
                Reject(() => fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId, observation, () => true));
                Equal(before, Snapshot(fixture)); return Task.CompletedTask;
            });
        await test("Gateway observation rejects denied authority", () =>
        {
            var before = Snapshot(fixture);
            Check(fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId, Observation(Publication(fixture, initial.EventId)), () => false) is null, "Denied authority cannot acknowledge");
            Equal(before, Snapshot(fixture)); return Task.CompletedTask;
        });
        await test("Gateway observation rolls back late authority revocation", () =>
        {
            var before = Snapshot(fixture); var calls = 0;
            var receipt = fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId, Observation(Publication(fixture, initial.EventId)), () => ++calls < 3);
            Equal(3, calls); Check(receipt is null, "Post-write authority revocation cannot acknowledge");
            Equal(before, Snapshot(fixture)); return Task.CompletedTask;
        });
        GatewayObservedConfiguration? accepted = null; GatewayProjectionAcknowledgement? acknowledged = null;
        await test("Exact observed configuration is durably acknowledged and replay emits once", () =>
        {
            accepted = Observation(Publication(fixture, initial.EventId)); var outbox = fixture.Database.Outbox(take: 100).Length;
            acknowledged = fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId, accepted, () => true);
            Check(acknowledged is not null, "Observed receipt exists"); Equal("acknowledged", acknowledged!.Phase);
            Equal(initial.EventId, acknowledged.EventId); Equal(initial.AggregateVersion, acknowledged.AggregateVersion);
            Equal(Json(accepted), Json(acknowledged.Gateway)); Equal(outbox + 1, fixture.Database.Outbox(take: 100).Length);
            var before = Snapshot(fixture);
            Equal(Json(acknowledged), Json(fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId, accepted, () => true)));
            Equal(before, Snapshot(fixture)); return Task.CompletedTask;
        });
        await test("Observed Gateway receipt is immutable", () =>
        {
            var before = Snapshot(fixture);
            Reject(() => fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId,
                accepted! with { ObservedAt = Utc(DateTimeOffset.Parse(accepted!.ObservedAt).AddMilliseconds(-1)) }, () => true));
            Equal(before, Snapshot(fixture)); return Task.CompletedTask;
        });
        await test("Gateway publication returns detached nested snapshots", () =>
        {
            var first = Publication(fixture, initial.EventId); var second = Publication(fixture, initial.EventId);
            Check(!ReferenceEquals(first.Projection, second.Projection) && !ReferenceEquals(first.Projection.Limits, second.Projection.Limits)
                && !ReferenceEquals(first.Projection.Limits.Scope.Environments, second.Projection.Limits.Scope.Environments), "Readers cannot mutate persisted or another reader's state");
            Equal(Json(first), Json(second)); Equal(Json(acknowledged), Json(fixture.Store.ReadGatewayAcknowledgement(fixture.ContractId, initial.EventId)));
            return Task.CompletedTask;
        });
        await test("Observed Gateway acknowledgement survives Fabric reopen", () =>
        {
            using var reopened = new FabricDatabase(fixture.DatabasePath);
            var store = new ContractPolicyStore(reopened, fixture.Access, fixture.Registry, fixture.Module, TimeProvider.System);
            Equal(Json(Publication(fixture, initial.EventId)), Json(store.ReadGatewayPublication(fixture.ContractId, initial.EventId)));
            Equal(Json(acknowledged), Json(store.ReadGatewayAcknowledgement(fixture.ContractId, initial.EventId)));
            var before = Json(reopened.Outbox(take: 100));
            Equal(Json(acknowledged), Json(store.RecordGatewayObservation(fixture.ContractId, initial.EventId, accepted!, () => true)));
            Equal(before, Json(reopened.Outbox(take: 100))); return Task.CompletedTask;
        });
        await test("Shared allocation drift vetoes an old Gateway acknowledgement", () =>
        {
            var limits = fixture.Store.ReadAllocation(initial.Projection.Limits.AllocationId)!;
            fixture.Database.Transaction(() =>
            {
                fixture.Database.Put("contract-policy.allocation", limits.AllocationId,
                    limits with { ConfigRevision = limits.ConfigRevision + 1 });
                return true;
            });
            try
            {
                var before = Snapshot(fixture);
                Check(fixture.Store.ReadGatewayAcknowledgement(fixture.ContractId, initial.EventId) is null,
                    "Another publication's shared allocation generation supersedes this Gateway configuration");
                RejectOrNull(() => fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId, accepted!, () => true));
                Equal(before, Snapshot(fixture));
            }
            finally
            {
                fixture.Database.Transaction(() => { fixture.Database.Put("contract-policy.allocation", limits.AllocationId, limits); return true; });
            }
            return Task.CompletedTask;
        });
        await test("Current duplicate event freezes pending publication", () =>
        {
            var duplicate = initial with { EventId = "synthetic-current-gateway-duplicate" };
            Equal("duplicate", Apply(fixture, duplicate).Status); Equal(duplicate.EventId, Publication(fixture, duplicate.EventId).EventId);
            Check(fixture.Store.ReadGatewayAcknowledgement(fixture.ContractId, duplicate.EventId) is null, "Duplicate cannot fabricate observation");
            return Task.CompletedTask;
        });
        await test("Superseded and unseen stale projection cannot acknowledge old configuration", () =>
        {
            Equal("applied", Apply(fixture, fixture.Amend(p => p)).Status); var before = Snapshot(fixture);
            RejectOrNull(() => fixture.Store.RecordGatewayObservation(fixture.ContractId, initial.EventId, accepted!, () => true));
            Check(fixture.Store.ReadGatewayAcknowledgement(fixture.ContractId, initial.EventId) is null, "Historical observation cannot acknowledge superseded projection");
            Equal(before, Snapshot(fixture));
            var stale = initial with { EventId = "synthetic-unseen-stale-gateway-event" }; Equal("stale", Apply(fixture, stale).Status);
            Check(fixture.Store.ReadGatewayPublication(fixture.ContractId, stale.EventId) is null, "Unseen stale projection has no publishable snapshot");
            return Task.CompletedTask;
        });
    }
    private static ProjectionResult Apply(PccFixture fixture, ContractProjectionEvent value) => fixture.Store.Apply(Json(value), fixture.ProducerPrincipal(), fixture.Binding, () => true);
    private static GatewayPublicationSnapshot Publication(PccFixture fixture, string eventId) => fixture.Store.ReadGatewayPublication(fixture.ContractId, eventId) ?? throw new InvalidOperationException("Missing publication");
    private static GatewayObservedConfiguration Observation(GatewayPublicationSnapshot p) => new(p.Projection.ContractVersion, p.Projection.Limits.AllocationId,
        p.Projection.Limits.CanonicalScope, p.Projection.Limits.ConfigRevision!.Value, p.LimitsSha256, Utc(DateTimeOffset.UtcNow.AddMilliseconds(-5)));
    private static string Snapshot(PccFixture fixture) => Json(new { Projection = fixture.Database.All<JsonElement>("contract-policy.projection"),
        Publication = fixture.Store.ReadGatewayPublication(fixture.ContractId, fixture.Event.EventId), Receipt = fixture.Store.ReadGatewayAcknowledgement(fixture.ContractId, fixture.Event.EventId),
        Outbox = fixture.Database.Outbox(take: 100), Business = fixture.BusinessSnapshot() });
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}"); }
    private static void Reject(Func<GatewayProjectionAcknowledgement?> action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Mismatched or mutable observation was accepted");
    }
    private static void RejectOrNull(Func<GatewayProjectionAcknowledgement?> action)
    {
        GatewayProjectionAcknowledgement? result;
        try { result = action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        Check(result is null, "Superseded observation was acknowledged");
    }
}
