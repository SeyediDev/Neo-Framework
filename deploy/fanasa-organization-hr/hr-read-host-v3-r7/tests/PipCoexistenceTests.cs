using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;
using Fanasa.HrReadPdp.Tests;

namespace Fanasa.HrReadHost.Tests;

// Real native PipStore + legacy evaluator + HR3 source/service, all on the SAME
// disposable Fabric instance. This suite never fetches an external source.
public static class PipCoexistenceTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        { count++; if (!value) throw new InvalidOperationException("HR3 PIP coexistence failed: " + label); }

        using (var f = await Fixture.CreateAsync())
        {
            var before = f.Core.Fingerprint();
            Check((await f.ReadAsync())?.Evidence.ValidUntil == FabricSourceTests.Format(FabricSourceTests.Now.AddSeconds(5)),
                "a registered PIP without dependent policies does not alter the native grant witness");
            Check(f.Core.Fingerprint() == before, "unused PIP source read writes nothing");
        }

        // Preserve the CURRENT native EvaluateForReadOnlyHost Enforce=false behavior.
        // This is deliberately different from calendar simulation-only filtering and
        // remains an explicit owner decision; this package does not edit AccessPolicies.
        foreach (var enforce in new[] { false, true })
        {
            using var f = await Fixture.CreateAsync();
            f.Publish("Deny", "false", enforce);
            var before = f.Core.Fingerprint();
            var native = f.Native();
            Check(native.Decision == "Permit" && native.Obligations.Length == 0
                && native.Pip?.FreshUntil == FabricSourceTests.Now.AddSeconds(2),
                $"native nonmatching PIP Deny leaves the direct grant Permit enforce={enforce}");
            Check((await f.ReadAsync())?.Evidence.ValidUntil == FabricSourceTests.Format(FabricSourceTests.Now.AddSeconds(2)),
                $"HR3 witness cannot outlive actual native PIP freshness enforce={enforce}");
            Check(f.Core.Fingerprint() == before, "native PIP evaluation and HR3 projection change no records or outbox");
            var writer = new Writer();
            var service = f.Service();
            Check(await service.ExecuteAsync(f.Core.Transport.Request, f.Core.Transport.Caller, writer),
                "real HR3 service serializes the bounded native-grant Permit");
            using var json = JsonDocument.Parse(writer.Bytes!);
            Check(writer.ValidUntil == FabricSourceTests.Now.AddSeconds(2)
                && json.RootElement.GetProperty("validUntil").GetDecimal()
                    == FabricSourceTests.Now.AddSeconds(2).ToUnixTimeMilliseconds() / 1000m
                && !json.RootElement.GetProperty("admissionConfirmed").GetBoolean(),
                "serialized transport deadline preserves exact PIP expiry and no admission claim");
            Check(f.Core.Fingerprint() == before, "complete PIP-bound response performs no Fabric/registry/outbox writes");
            f.Core.Transport.SetTime(FabricSourceTests.Now.AddSeconds(2));
            Check(await f.ReadAsync() is null && f.Native().Decision == "Indeterminate",
                "PIP freshness endpoint is exclusive despite a valid native direct grant");
            Check(f.Core.Fingerprint() == before, "expired PIP cannot be repaired or refreshed by the read-only adapter");
        }

        foreach (var enforce in new[] { false, true })
        foreach (var effect in new[] { "Deny", "Permit" })
        {
            using var f = await Fixture.CreateAsync();
            f.Publish(effect, "true", enforce);
            var before = f.Core.Fingerprint();
            var native = f.Native();
            Check(effect == "Deny" ? native.Decision == "Deny"
                : native.Decision == "Permit" && native.Obligations.Contains("audit"),
                $"native matching {effect} keeps its decision and obligations enforce={enforce}");
            Check(await f.ReadAsync() is null,
                "HR3 cannot bypass matching native denial or fulfill a PIP Permit audit obligation");
            Check(f.Core.Fingerprint() == before, "matching policy rejection writes nothing");
        }

        foreach (var invalid in new[] { "disabled", "missing", "wrong-subject", "wrong-tenant", "generation", "stale" })
        {
            using var f = await Fixture.CreateAsync();
            f.Publish("Deny", "false", true);
            if (invalid == "disabled") f.Pip.SetEnabled(f.Core.Tenant.Id, f.Source.Id, false);
            if (invalid == "missing") f.Core.Transport.Delete(PipStore.SnapshotDocumentKind, f.SnapshotKey);
            if (invalid is "wrong-subject" or "wrong-tenant" or "generation")
                f.Core.Transport.Put(PipStore.SnapshotDocumentKind, f.SnapshotKey, invalid switch
                {
                    "wrong-subject" => f.Snapshot with { Subject = "unrelated-subject" },
                    "wrong-tenant" => f.Snapshot with { TenantId = Guid.NewGuid() },
                    _ => f.Snapshot with { Generation = f.Snapshot.Generation + 1 }
                });
            if (invalid == "stale") f.Core.Transport.SetTime(FabricSourceTests.Now.AddSeconds(3));
            var before = f.Core.Fingerprint();
            Check(f.Native().Decision == "Indeterminate", invalid + " native PIP fails closed before HR3");
            Check(await f.ReadAsync() is null, invalid + " PIP cannot use the otherwise valid native grant");
            Check(f.Core.Fingerprint() == before, invalid + " PIP rejection preserves every Fabric/registry/outbox row");
        }

        using (var f = await Fixture.CreateAsync())
        {
            f.Publish("Deny", "false", true);
            var before = f.Core.Fingerprint();
            Check(await f.Core.ReadAsync() is null,
                "an adapter whose native evaluator has no PipStore cannot invent a PIP witness");
            Check(f.Core.Fingerprint() == before, "missing native PIP registration writes nothing");
        }

        using (var f = await Fixture.CreateAsync())
        {
            f.Publish("Deny", "false", true);
            var second = f.Pip.Register(f.Core.Tenant.Id, "second synthetic PIP", "https://second.invalid/pip", "second.invalid",
                null, maxAgeSeconds: 30, enabled: true);
            f.Save(second, FabricSourceTests.Now.AddSeconds(-29));
            f.Publish("Deny", "false", true, second);
            var before = f.Core.Fingerprint();
            Check(f.Native().Decision == "Permit", "two native nonmatching Deny sources currently leave a direct-grant Permit");
            Check(await f.ReadAsync() is null,
                "single-provenance AccessDecision cannot stand in for a multi-source HR3 freshness witness");
            Check(f.Core.Fingerprint() == before, "unsupported multi-source witness fails closed without writes");
        }

        foreach (var change in new[] { "revoke", "expire", "matching-deny", "shorten" })
        {
            using var f = await Fixture.CreateAsync();
            f.Publish("Deny", "false", true);
            var source = new ObservedSource(f.SourceReader());
            string? afterChange = null;
            source.Before = read =>
            {
                if (read != 2) return;
                if (change == "revoke") f.Pip.SetEnabled(f.Core.Tenant.Id, f.Source.Id, false);
                if (change == "expire") f.Core.Transport.SetTime(FabricSourceTests.Now.AddSeconds(2));
                if (change == "matching-deny") f.Save(f.Source, FabricSourceTests.Now.AddSeconds(-27), "false", "changed-2");
                if (change == "shorten")
                {
                    var third = f.Pip.Register(f.Core.Tenant.Id, "shorter synthetic PIP", "https://short.invalid/pip", "short.invalid",
                        null, maxAgeSeconds: 30, enabled: true);
                    f.Save(third, FabricSourceTests.Now.AddSeconds(-29));
                    var state = f.Policies.Read(f.Core.Tenant.Id);
                    f.Core.Transport.Put("access.policy", f.Core.Tenant.Id.ToString("D"),
                        state with { Revision = state.Revision + 1, Patterns = state.Patterns.Select(x => x with { PipSourceId = third.Id }).ToArray() });
                }
                afterChange = f.Core.Fingerprint();
            };
            var writer = new Writer();
            var service = new HrReadPdpService(f.Core.Transport.Module, new Factory(source), f.Core.Transport.Time);
            Check(!await service.ExecuteAsync(f.Core.Transport.Request, f.Core.Transport.Caller, writer)
                && source.Reads == 2 && writer.Writes == 0,
                change + " committed after projection is observed by the independent post-serialization snapshot");
            Check(afterChange is not null && f.Core.Fingerprint() == afterChange,
                change + " final denial neither undoes the fixture revocation nor writes any response or authority");
        }
        return count;
    }

    private sealed class Fixture(CentralHostRecordsTests.Fixture core) : IDisposable
    {
        public CentralHostRecordsTests.Fixture Core { get; } = core;
        public PipStore Pip { get; private set; } = null!;
        public AccessPolicyStore Policies { get; private set; } = null!;
        public PipSourceDefinition Source { get; private set; } = null!;
        public PipAttributeSnapshot Snapshot { get; private set; } = null!;
        public string SnapshotKey => $"{Core.Tenant.Id:D}:{Source.Id:D}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Core.Member.KeycloakSubject))).ToLowerInvariant()}";
        public static async Task<Fixture> CreateAsync()
        {
            var result = new Fixture(await CentralHostRecordsTests.CreateFixtureAsync());
            try
            {
                result.Pip = new(result.Core.Transport.Database, result.Core.Access);
                result.Policies = new(result.Core.Transport.Database, result.Core.Access, pip: result.Pip);
                result.Source = result.Pip.Register(result.Core.Tenant.Id, "Synthetic HR3 PIP", "https://directory.invalid/pip",
                    "directory.invalid", null, maxAgeSeconds: 30, enabled: true);
                result.Snapshot = result.Save(result.Source, FabricSourceTests.Now.AddSeconds(-28));
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        public PipAttributeSnapshot Save(PipSourceDefinition source, DateTimeOffset observed,
            string value = "true", string version = "synthetic-1")
        {
            var attributes = new Dictionary<string, string> { ["employment.active"] = value };
            return Pip.SaveSnapshot(source, new(source.TenantId, Core.Member.KeycloakSubject, 1, attributes,
                version, null, observed, PipStore.HashAttributes(source.TenantId, Core.Member.KeycloakSubject, 1,
                    attributes, version, observed)), FabricSourceTests.Now);
        }
        public void Publish(string effect, string expected, bool enforce, PipSourceDefinition? source = null)
        {
            var draft = Policies.Draft(Core.Tenant.Id, new(Policies.Read(Core.Tenant.Id).Revision,
                "Synthetic HR3 attribute rule", "attribute", HrReadDesign.Permission, effect, null, null,
                null, null, null, null, "Synthetic HR3 PIP compatibility", Enforce: enforce,
                AttributeName: "employment.active", AttributeValue: expected, PipSourceId: (source ?? Source).Id), "author");
            Policies.Transition(Core.Tenant.Id, draft.Patterns[^1].Id, draft.Revision, "publish", "reviewer", "Synthetic independent review");
        }
        public AccessDecision Native() => Policies.EvaluateForReadOnlyHost(Core.Tenant.Id, Core.Member.KeycloakSubject,
            HrReadDesign.Permission, Core.Transport.Time.GetUtcNow(), false);
        public HrReadPdpFabricSource SourceReader() => Core.Transport.CreateSource(new Hr3CentralHostRecords(
            Core.Transport.Database, Core.Registry, Core.Access, Policies, Core.ProofSource, Core.Transport.Time));
        public ValueTask<AccessAttributeSnapshotData?> ReadAsync() => SourceReader().ReadAsync(Core.Transport.Query);
        public HrReadPdpService Service() => new(Core.Transport.Module, new Factory(SourceReader()), Core.Transport.Time);
        public void Dispose() => Core.Dispose();
    }
    private sealed class Factory(IAccessAttributeSnapshotSource source) : IHrReadPdpSourceFactory
    {
        public IAccessAttributeSnapshotSource Create(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller,
            TrustedAccessSnapshotQuery query) => source;
    }
    private sealed class ObservedSource(IAccessAttributeSnapshotSource inner) : IAccessAttributeSnapshotSource
    {
        public int Reads { get; private set; }
        public Action<int>? Before { get; set; }
        public ValueTask<AccessAttributeSnapshotData?> ReadAsync(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken = default)
        { Before?.Invoke(++Reads); return inner.ReadAsync(query, cancellationToken); }
    }
    private sealed class Writer : IHrReadPdpResponseWriter
    {
        public int Writes { get; private set; }
        public byte[]? Bytes { get; private set; }
        public DateTimeOffset ValidUntil { get; private set; }
        public ValueTask SendAsync(byte[] bytes, DateTimeOffset validUntil, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Writes++; Bytes = bytes.ToArray(); ValidUntil = validUntil; return ValueTask.CompletedTask; }
    }
}
