using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;

namespace Fanasa.HrReadPdp.Tests;

public static class ServiceTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool pass, string label) { count++; if (!pass) throw new InvalidOperationException("PDP service test failed: " + label); }
        using (var f = await FabricSourceTests.CreateFixtureAsync())
        {
            var before = f.DataFingerprint(); var source = new ObservedSource(f.CreateSource()); var writer = new Writer();
            var service = new HrReadPdpService(f.Module, new Factory(source), f.Time);
            Check(f.Query.AuthenticatedSubject.Scopes.SequenceEqual(new[] { "openid", "organization.hr.read" }), "ordered schema-compatible scopes derived from full verified raw scope");
            Check(await service.ExecuteAsync(f.Request, f.Caller, writer), "real core and temporary SQLite source produce response");
            Check(source.Reads == 2 && f.Records.Reads == 4, "two fresh source snapshots each correlate central records twice");
            Check(writer.Calls == 1 && writer.Bytes is { Length: > 0 and <= HrReadPdpService.MaximumResponseBytes }, "one completely serialized bounded response");
            using var json = JsonDocument.Parse(writer.Bytes!); var root = json.RootElement;
            Check(f.Module.Schemas.Validate("hr-read-pdp-response.v1.schema.json", JsonNode.Parse(writer.Bytes!)).Count == 0, "actual Permit response matches published wire schema");
            Check(root.GetProperty("transportVersion").GetString() == HrReadPdpWire.ResponseVersion, "versioned transport");
            Check(root.GetProperty("requestId").GetString() == f.Request.RequestId && root.GetProperty("rawTarget").GetString() == f.Request.RawTarget, "exact request and target echo");
            Check(root.GetProperty("tokenSha256").GetString() == f.Request.TokenSha256 && root.GetProperty("consumerTenantId").GetString() == f.Request.ConsumerTenantId, "identity and consumer binding");
            Check(root.GetProperty("contractVersion").GetInt64() == f.Request.ContractVersion && root.GetProperty("configRevision").GetInt64() == f.Request.ConfigRevision, "exact tuple revision binding");
            Check(root.GetProperty("ownership").GetProperty("registeredProductId").GetString() == f.Binding.RegisteredProductId, "central ownership identity");
            Check(root.GetProperty("decision").GetProperty("decision").GetString() == "Permit", "real HR3 evaluator Permit");
            Check(root.GetProperty("decision").GetProperty("obligations").GetArrayLength() == 2 && !root.GetProperty("admissionConfirmed").GetBoolean(), "Permit retains unfulfilled obligations and no admission receipt");
            Check(root.GetProperty("validUntil").GetDecimal() <= f.Request.RequestedAt + 5 && writer.ValidUntil <= f.Request.ValidUntil, "exclusive evidence ceiling");
            Check(root.GetProperty("allocationPeriodId").GetString() == "2026-10" && root.GetProperty("periodStartMs").GetInt64() < root.GetProperty("periodEndMs").GetInt64(), "current UTC allocation month");
            Check(f.DataFingerprint() == before, "entire database/outbox/ledger/replay state unchanged");
            Check(!Encoding.UTF8.GetString(writer.Bytes!).Contains("fixture.subject.token", StringComparison.Ordinal), "raw bearer never serialized");
        }
        using (var f = await FabricSourceTests.CreateFixtureAsync())
        {
            f.Projection = f.Projection with { Policy = f.Projection.Policy with { ExplicitDenyActions = [f.Request.Action] } };
            f.Published = f.Published with { Binding = f.Projection.Policy }; f.Seed();
            var writer = new Writer();
            Check(await new HrReadPdpService(f.Module, new Factory(f.CreateSource()), f.Time).ExecuteAsync(f.Request, f.Caller, writer), "current explicit-deny produces a valid decision transport");
            using var json = JsonDocument.Parse(writer.Bytes!); var decision = json.RootElement.GetProperty("decision");
            Check(f.Module.Schemas.Validate("hr-read-pdp-response.v1.schema.json", JsonNode.Parse(writer.Bytes!)).Count == 0, "actual Deny response matches published wire schema");
            Check(decision.GetProperty("decision").GetString() == "Deny" && decision.GetProperty("obligations").GetArrayLength() == 0
                && decision.GetProperty("validUntil").ValueKind == JsonValueKind.Null
                && json.RootElement.GetProperty("validUntil").GetDecimal() > f.Request.RequestedAt,
                "Deny has no permit lease or obligations but transport remains bounded");
        }
        async Task Reject(Action<FabricSourceTests.Fixture, ObservedSource>? change, string label, bool defaultFactory = false, bool defaultHost = false)
        {
            using var f = await FabricSourceTests.CreateFixtureAsync();
            var source = new ObservedSource(defaultHost ? f.CreateSource(new UnavailableHrReadPdpHostRecords()) : f.CreateSource());
            change?.Invoke(f, source); var writer = new Writer();
            var service = new HrReadPdpService(f.Module, defaultFactory ? null : new Factory(source), f.Time);
            Check(!await service.ExecuteAsync(f.Request, f.Caller, writer) && writer.Calls == 0, label);
        }
        await Reject(null, "missing source factory denies", defaultFactory: true);
        await Reject(null, "missing real central host adapter denies", defaultHost: true);
        await Reject((f, s) => s.Before = n => { if (n == 2) f.Records.Value = f.Records.Value! with { NativePermissionAllowed = false }; }, "native permission revoked between independent reads emits no bytes");
        await Reject((f, s) => s.Before = n => { if (n == 2) f.Records.Value = f.Records.Value! with { CallerDelegationAllowed = false }; }, "Gateway delegation revoked after serialization emits no bytes");
        await Reject((f, s) => s.Before = n => { if (n == 2) f.Records.Value = f.Records.Value! with { ValidUntil = FabricSourceTests.Now.AddSeconds(4) }; }, "shorter final evidence lease invalidates prepared deadline");
        await Reject((f, s) => s.Before = n => { if (n == 2) f.Put(HrReadPdpFabricSource.PolicyBucket, HrReadPdpFabricSource.PolicyKey(f.Query.Selector!), f.Published with { Active = false }); }, "committed policy revocation visible in new final snapshot");
        await Reject((f, s) => s.Before = n => { if (n == 2) f.Delete(HrReadPdpFabricSource.AllocationBucket, f.Request.AllocationId); }, "committed allocation removal visible in final snapshot");
        await Reject((f, s) => s.Before = n => { if (n == 2) f.SetTime(FabricSourceTests.Now.AddSeconds(5)); }, "exclusive deadline at final snapshot denies");
        await Reject((f, s) => s.Before = n => { if (n == 2) f.Http.Request.Headers["Authorization"] = "Bearer changed"; }, "native retained authentication proof rechecked after initial read");
        await Reject((_, s) => s.Before = _ => throw new IOException("isolated read failure"), "source read failure is unavailable without writes");
        using (var f = await FabricSourceTests.CreateFixtureAsync())
        {
            var request = new HrReadPdpRequest(f.Binding, f.Request.Target, f.Request.RawTarget, f.Request.RequestId,
                f.Request.RequestedAt, f.Request.TokenSha256, f.Request.TokenExpiresAt);
            var writer = new Writer(); var source = new ObservedSource(f.CreateSource());
            Check(!await new HrReadPdpService(f.Module, new Factory(source), f.Time).ExecuteAsync(request, f.Caller, writer)
                && source.Reads == 0 && writer.Calls == 0, "proof cannot authorize another parsed request instance");
        }
        using (var f = await FabricSourceTests.CreateFixtureAsync())
        {
            var before = f.DataFingerprint();
            var service = new HrReadPdpService(f.Module, new ExistingFabricHrReadPdpSourceFactory(f.Database, f.Module.Projections, f.Records, f.Time), f.Time);
            Check(await HrReadPdpHttpEndpoint.ExecuteAsync(f.Http, f.CallerSettings, f.Binding, service, f.Certificates, f.Time),
                "HTTP parser and explicit authentication integrate with real SQLite and core evaluator");
            Check(f.Http.Response.StatusCode == 200 && f.Http.Response.ContentType == "application/json"
                && f.Http.Response.ContentLength == f.Http.Response.Body.Length && f.Http.Response.Headers.CacheControl == "no-store", "complete successful HTTP metadata");
            f.Http.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(f.Http.Response.Body);
            Check(json.RootElement.GetProperty("decision").GetProperty("decision").GetString() == "Permit"
                && !json.RootElement.GetProperty("admissionConfirmed").GetBoolean(), "HTTP response carries decision with obligations unfulfilled");
            Check(f.DataFingerprint() == before, "complete HTTP path leaves authoritative database unchanged");
        }
        using (var f = await FabricSourceTests.CreateFixtureAsync())
        {
            var writer = new Writer { Failure = new IOException("isolated send failure") }; var source = new ObservedSource(f.CreateSource()); var threw = false;
            try { await new HrReadPdpService(f.Module, new Factory(source), f.Time).ExecuteAsync(f.Request, f.Caller, writer); }
            catch (IOException) { threw = true; }
            Check(threw && writer.Calls == 1 && source.Reads == 2, "transport send failure propagates after final gate");
        }
        using (var f = await FabricSourceTests.CreateFixtureAsync())
        {
            using var cancellation = new CancellationTokenSource(); var writer = new Writer(); var source = new ObservedSource(f.CreateSource());
            source.After = n => { if (n == 1) cancellation.Cancel(); }; var threw = false;
            try { await new HrReadPdpService(f.Module, new Factory(source), f.Time).ExecuteAsync(f.Request, f.Caller, writer, cancellation.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw && source.Reads == 1 && writer.Calls == 0, "cancellation after initial read prevents final read/send and propagates");
        }
        using (var f = await FabricSourceTests.CreateFixtureAsync())
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var source = new ObservedSource(f.CreateSource()); var writer = new Writer(); var threw = false;
            try { await new HrReadPdpService(f.Module, new Factory(source), f.Time).ExecuteAsync(f.Request, f.Caller, writer, cancellation.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw && source.Reads == 0 && writer.Calls == 0, "pre-cancelled request has no authority or transport effects");
        }
        return count;
    }

    private sealed class Factory(IAccessAttributeSnapshotSource source) : IHrReadPdpSourceFactory
    {
        public IAccessAttributeSnapshotSource? Create(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller, TrustedAccessSnapshotQuery query) => source;
    }
    private sealed class ObservedSource(IAccessAttributeSnapshotSource inner) : IAccessAttributeSnapshotSource
    {
        public int Reads { get; private set; }
        public Action<int>? Before { get; set; }
        public Action<int>? After { get; set; }
        public async ValueTask<AccessAttributeSnapshotData?> ReadAsync(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken = default)
        {
            Reads++; Before?.Invoke(Reads); var data = await inner.ReadAsync(query, cancellationToken); After?.Invoke(Reads); return data;
        }
    }
    private sealed class Writer : IHrReadPdpResponseWriter
    {
        public int Calls { get; private set; }
        public byte[]? Bytes { get; private set; }
        public DateTimeOffset ValidUntil { get; private set; }
        public IOException? Failure { get; init; }
        public ValueTask SendAsync(byte[] bytes, DateTimeOffset validUntil, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Bytes = bytes.ToArray(); ValidUntil = validUntil;
            if (Failure is not null) throw Failure;
            return ValueTask.CompletedTask;
        }
    }
}
