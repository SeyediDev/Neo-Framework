using System.Text;
using System.Text.Json;

namespace Fanasa.HrReadCandidate;

/// <summary>Local seam fixtures only. No real Fabric, gateway, TLS, identity or deployment is accepted here.</summary>
internal static class CoordinatorTests
{
    private const string Tenant = "7398590f-2d87-41ce-beda-19fb8e2a45fd";
    private const string Chart = "/api/organization-hr/v1/tenants/" + Tenant + "/chart";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly HrReadTarget ChartTarget = new(HrReadEndpointKind.Chart, Guid.Parse(Tenant), null);

    public static async Task RunAsync()
    {
        await DefaultAuthorityDeniesAsync();
        await InvalidRequestsNeverReachAuthorityAsync();
        await MismatchedAdmissionDeniesAsync();
        await SuccessCommitsNonceThenReadsThenFreshGateAsync();
        await SimulatedReplayDeniesAsync();
        await DetachedJsonSurvivesSourceDisposalAsync();
        await ByteLimitPrecedesFreshGateAsync();
        await RevocationAfterReadDeniesAsync();
        await NotFoundUsesFreshGateAsync();
        await ExclusiveDeadlinesAsync();
        await CancellationAtEachBoundaryAsync();
        await AuthorityAndStoreFailuresDenyWithoutPayloadAsync();
        await WriterFailurePropagatesAsync();
        await HistoryRevisionUsesBackendInt64Async();
    }

    private static async Task DefaultAuthorityDeniesAsync()
    {
        var f = new Fixture();
        var coordinator = new HrReadCoordinator(f.Fabric, time: f.Time);
        var outcome = await coordinator.ExecuteAsync("GET", Chart, Stream.Null, f.Writer);
        Check.True(outcome == HrReadOutcome.Denied, "unconfigured authority fails closed");
        Check.True(f.Events.Count == 0 && f.Writer.Calls == 0, "default denial never enters Fabric or writes");
    }

    private static async Task InvalidRequestsNeverReachAuthorityAsync()
    {
        (string Method, string Target, byte[] Body)[] requests =
        [
            ("POST", Chart, []), ("get", Chart, []), ("HEAD", Chart, []),
            ("GET", Chart + "?page=1", []), ("GET", Chart + "/", []),
            ("GET", Chart.Replace("/chart", "/commands"), []),
            ("GET", Chart, [0]), ("GET", Chart, Encoding.UTF8.GetBytes(" ")),
            ("GET", Chart, Encoding.UTF8.GetBytes("{}"))
        ];
        foreach (var request in requests)
        {
            var f = new Fixture();
            using var body = new MemoryStream(request.Body);
            Check.True(await f.Coordinator.ExecuteAsync(request.Method, request.Target, body, f.Writer) == HrReadOutcome.Denied,
                "unsupported method/target or nonempty body denies");
            Check.True(f.Authority.Calls == 0 && f.Events.Count == 0 && f.Writer.Calls == 0,
                "invalid request has no authentication, nonce, snapshot or response side effects");
        }
    }

    private static async Task MismatchedAdmissionDeniesAsync()
    {
        var f = new Fixture();
        f.Authority.Admission = new Admission(new(HrReadEndpointKind.History, Guid.Parse(Tenant), 1), 4096, Now.AddMinutes(1));
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied, "admission for another endpoint cannot authorize chart");
        Check.True(f.Authority.Calls == 1 && f.Fabric.NonceCalls == 0 && f.Writer.Calls == 0,
            "target mismatch rejects before nonce transaction");

        f = new Fixture();
        f.Authority.Admission = new Admission(ChartTarget with { TenantId = Guid.NewGuid() }, 4096, Now.AddMinutes(1));
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied && f.Fabric.NonceCalls == 0 && f.Writer.Calls == 0,
            "admission for another tenant cannot authorize this target");
    }

    private static async Task SuccessCommitsNonceThenReadsThenFreshGateAsync()
    {
        var f = new Fixture();
        f.Fabric.OnGate = () =>
        {
            Check.True(f.Fabric.NonceCommitted && !f.Fabric.ReadSnapshotOpen,
                "fresh gate begins after committed nonce and closed read snapshot");
            Check.True(f.Writer.Calls == 0, "no bytes are sent before final gate");
        };
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Sent, "valid zero-body GET reaches response seam");
        Check.True(string.Join(",", f.Events) == "authority,nonce.begin,nonce.commit,read.begin,read.end,gate.begin,gate.end,send",
            "independent nonce commit precedes read and separate fresh gate precedes send");
        Check.True(f.Writer.Status == 200 && f.Writer.CacheControl == "no-store", "success is JSON response with no-store contract");
        Check.True(Encoding.UTF8.GetString(f.Writer.Bytes) == "{\"fixture\":true}", "complete detached response bytes are sent");
    }

    private static async Task SimulatedReplayDeniesAsync()
    {
        var f = new Fixture();
        f.Fabric.SimulateReplayClaim = true;
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Sent, "first fixture nonce claim succeeds");
        var readCalls = f.Fabric.ReadCalls;
        var gateCalls = f.Fabric.GateCalls;
        var sends = f.Writer.Calls;
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied, "same fixture adapter rejects its already consumed nonce");
        Check.True(f.Fabric.NonceCalls == 2 && f.Fabric.ReadCalls == readCalls && f.Fabric.GateCalls == gateCalls && f.Writer.Calls == sends,
            "replay rejection neither reads nor emits another response");

        var vetoed = new Fixture();
        vetoed.Fabric.SimulateReplayClaim = true;
        vetoed.Fabric.GateAllows = false;
        Check.True(await vetoed.ExecuteAsync() == HrReadOutcome.Denied && vetoed.Fabric.NonceCommitted,
            "late veto keeps fixture nonce consumed");
        vetoed.Fabric.GateAllows = true;
        Check.True(await vetoed.ExecuteAsync() == HrReadOutcome.Denied && vetoed.Fabric.ReadCalls == 1 && vetoed.Writer.Calls == 0,
            "late veto does not refund nonce for another attempt");
    }

    private static async Task DetachedJsonSurvivesSourceDisposalAsync()
    {
        var f = new Fixture();
        var disposed = false;
        JsonElement original = default;
        f.Fabric.ReadResult = () =>
        {
            using var source = JsonDocument.Parse("{\"name\":\"fixture\",\"units\":[1,2,3]}");
            original = source.RootElement;
            var result = MaterializedHrRead.Existing(original);
            source.Dispose();
            disposed = true;
            return result;
        };
        f.Fabric.OnGate = () => Check.True(disposed, "source document disposed before fresh gate");
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Sent, "cloned JSON survives source snapshot/document disposal");
        Check.Throws<ObjectDisposedException>(() => original.GetRawText(), "fixture source really is disposed");
        using var delivered = JsonDocument.Parse(f.Writer.Bytes);
        Check.True(delivered.RootElement.GetProperty("units").GetArrayLength() == 3, "detached payload is fully materialized");
        Check.Throws<ArgumentException>(() => MaterializedHrRead.Existing(default), "undefined JSON cannot cross materialization boundary");
    }

    private static async Task ByteLimitPrecedesFreshGateAsync()
    {
        var f = new Fixture(maximumBytes: 3);
        f.Fabric.ReadResult = () => Detached("\"ok\""); // Four UTF-8 bytes including quotes.
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied, "response one byte over cap fails closed");
        Check.True(f.Fabric.NonceCommitted && f.Fabric.ReadCalls == 1 && f.Fabric.GateCalls == 0 && f.Writer.Calls == 0,
            "serialization cap rejects after read but before fresh gate and send");

        f = new Fixture(maximumBytes: 4);
        f.Fabric.ReadResult = () => Detached("\"ok\"");
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Sent && f.Writer.Bytes.Length == 4,
            "response exactly at byte cap is permitted");

        f = new Fixture(maximumBytes: 8);
        f.Fabric.ReadResult = () => Detached(JsonSerializer.Serialize(new string('x', 50000)));
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied && f.Fabric.GateCalls == 0 && f.Writer.Calls == 0,
            "large buffered serialization still obeys byte cap before fresh gate");
    }

    private static async Task RevocationAfterReadDeniesAsync()
    {
        var f = new Fixture();
        var currentAccess = true;
        f.Fabric.ReadResult = () => { currentAccess = false; return Detached("{\"sensitive\":\"fixture\"}"); };
        f.Fabric.GateDecision = () => currentAccess;
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied, "revocation between read and fresh gate vetoes payload");
        Check.True(f.Fabric.ReadCalls == 1 && f.Fabric.GateCalls == 1 && f.Writer.Calls == 0 && f.Fabric.NonceCommitted,
            "revoked materialized data is discarded while consumed nonce remains");
    }

    private static async Task NotFoundUsesFreshGateAsync()
    {
        var f = new Fixture();
        f.Fabric.ReadResult = MaterializedHrRead.NotFound;
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Sent, "authorized not-found can be delivered");
        Check.True(f.Fabric.GateCalls == 1 && f.Writer.Status == 404 && f.Writer.Bytes.Length == 0 && f.Writer.CacheControl == "no-store",
            "404 passes fresh gate and sends empty no-store response");

        f = new Fixture();
        f.Fabric.ReadResult = MaterializedHrRead.NotFound;
        f.Fabric.GateAllows = false;
        Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied && f.Fabric.GateCalls == 1 && f.Writer.Calls == 0,
            "404 existence signal is suppressed when fresh gate denies");
    }

    private static async Task ExclusiveDeadlinesAsync()
    {
        foreach (var deadline in new[] { Now.AddTicks(-1), Now })
        {
            var f = new Fixture(deadline: deadline);
            Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied && f.Fabric.NonceCalls == 0 && f.Writer.Calls == 0,
                "expired or exact-exclusive admission deadline rejects before nonce");
        }
        var near = new Fixture(deadline: Now.AddTicks(1));
        Check.True(await near.ExecuteAsync() == HrReadOutcome.Sent, "admission is current one tick before exclusive deadline");

        var afterNonce = new Fixture(deadline: Now.AddSeconds(1));
        afterNonce.Fabric.OnNonceCommitted = () => afterNonce.Time.UtcNow = Now.AddSeconds(1);
        Check.True(await afterNonce.ExecuteAsync() == HrReadOutcome.Denied && afterNonce.Fabric.ReadCalls == 0 && afterNonce.Writer.Calls == 0,
            "deadline reached after nonce prevents snapshot");

        var afterRead = new Fixture(deadline: Now.AddSeconds(1));
        afterRead.Fabric.ReadResult = () => { afterRead.Time.UtcNow = Now.AddSeconds(1); return Detached("{}"); };
        Check.True(await afterRead.ExecuteAsync() == HrReadOutcome.Denied && afterRead.Fabric.GateCalls == 0 && afterRead.Writer.Calls == 0,
            "deadline reached after read prevents final gate and send");

        var afterGate = new Fixture(deadline: Now.AddSeconds(1));
        afterGate.Fabric.OnGate = () => afterGate.Time.UtcNow = Now.AddSeconds(1);
        Check.True(await afterGate.ExecuteAsync() == HrReadOutcome.Denied && afterGate.Fabric.GateCalls == 1 && afterGate.Writer.Calls == 0,
            "deadline reached during final gate is checked again before send");
    }

    private static async Task CancellationAtEachBoundaryAsync()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var f = new Fixture();
            await Check.ThrowsAsync<OperationCanceledException>(() => f.ExecuteAsync(cancelled.Token), "pre-cancelled request propagates cancellation");
            Check.True(f.Authority.Calls == 0 && f.Fabric.NonceCalls == 0 && f.Writer.Calls == 0, "pre-cancellation has no effects");
        }
        foreach (var stage in new[] { "authority", "nonce", "read", "gate" })
        {
            using var cancelled = new CancellationTokenSource();
            var f = new Fixture();
            if (stage == "authority") f.Authority.OnResolve = cancelled.Cancel;
            if (stage == "nonce") f.Fabric.OnNonceCommitted = cancelled.Cancel;
            if (stage == "read") f.Fabric.ReadResult = () => { cancelled.Cancel(); return Detached("{}"); };
            if (stage == "gate") f.Fabric.OnGate = cancelled.Cancel;
            await Check.ThrowsAsync<OperationCanceledException>(() => f.ExecuteAsync(cancelled.Token), "cancellation after " + stage + " propagates");
            Check.True(f.Writer.Calls == 0, "cancellation after " + stage + " cannot leak payload");
            Check.True(f.Fabric.NonceCalls == (stage == "authority" ? 0 : 1), "nonce boundary for cancellation after " + stage);
            Check.True(f.Fabric.ReadCalls == (stage is "read" or "gate" ? 1 : 0), "read boundary for cancellation after " + stage);
            Check.True(f.Fabric.GateCalls == (stage == "gate" ? 1 : 0), "fresh gate boundary for cancellation after " + stage);
        }
    }

    private static async Task AuthorityAndStoreFailuresDenyWithoutPayloadAsync()
    {
        foreach (var stage in new[] { "authority", "nonce", "read", "gate" })
        {
            var f = new Fixture();
            if (stage == "authority") f.Authority.Failure = new InvalidOperationException("fixture authority failure");
            else f.Fabric.FailureAt = stage;
            Check.True(await f.ExecuteAsync() == HrReadOutcome.Denied && f.Writer.Calls == 0,
                stage + " exception is a closed denial with no payload write");
        }
        var absent = new Fixture();
        absent.Authority.Admission = null;
        Check.True(await absent.ExecuteAsync() == HrReadOutcome.Denied && absent.Fabric.NonceCalls == 0 && absent.Writer.Calls == 0,
            "explicit unavailable authority has no Fabric effects");

        var nonceDenied = new Fixture();
        nonceDenied.Fabric.NonceAllows = false;
        Check.True(await nonceDenied.ExecuteAsync() == HrReadOutcome.Denied && nonceDenied.Fabric.ReadCalls == 0
            && nonceDenied.Fabric.GateCalls == 0 && nonceDenied.Writer.Calls == 0, "nonce veto cannot read or send");

        var foreignCancellation = new Fixture();
        foreignCancellation.Authority.Failure = new OperationCanceledException("fixture unrelated token");
        Check.True(await foreignCancellation.ExecuteAsync() == HrReadOutcome.Denied && foreignCancellation.Writer.Calls == 0,
            "unrelated cancellation exception fails closed when caller token is not cancelled");
    }

    private static async Task WriterFailurePropagatesAsync()
    {
        var f = new Fixture();
        f.Writer.Failure = new IOException("fixture transmission failure");
        await Check.ThrowsAsync<IOException>(() => f.ExecuteAsync(), "transmission failure must propagate rather than become denial");
        Check.True(f.Fabric.GateCalls == 1 && f.Writer.Calls == 1, "writer called only after fresh gate; failed transmission is not retried");
    }

    private static async Task HistoryRevisionUsesBackendInt64Async()
    {
        var target = new HrReadTarget(HrReadEndpointKind.History, Guid.Parse(Tenant), long.MaxValue);
        var f = new Fixture(target: target);
        Check.True(await f.ExecuteAsync(rawTarget: Chart.Replace("/chart", "/history/9223372036854775807")) == HrReadOutcome.Sent,
            "maximum positive Int64 history revision reaches read seam");
        Check.True(f.Authority.LastTarget == target && f.Fabric.LastAdmission?.Target == target,
            "history revision and tenant remain exact through authority and Fabric");

        f = new Fixture(target: target);
        Check.True(await f.ExecuteAsync(rawTarget: Chart.Replace("/chart", "/history/9223372036854775808")) == HrReadOutcome.Denied
            && f.Authority.Calls == 0 && f.Writer.Calls == 0, "Int64-overflow history revision denies before admission");
    }

    private static MaterializedHrRead Detached(string json)
    { using var document = JsonDocument.Parse(json); return MaterializedHrRead.Existing(document.RootElement); }

    private sealed class Admission(HrReadTarget target, int maximumBytes, DateTimeOffset deadline)
        : HrReadAdmission(target, maximumBytes, deadline) { }

    private sealed class Fixture
    {
        public List<string> Events { get; } = [];
        public FixtureTimeProvider Time { get; } = new();
        public FixtureAuthority Authority { get; }
        public FixtureFabric Fabric { get; }
        public FixtureWriter Writer { get; }
        public HrReadCoordinator Coordinator { get; }
        public Fixture(int maximumBytes = 4096, DateTimeOffset? deadline = null, HrReadTarget? target = null)
        {
            Authority = new(Events) { Admission = new Admission(target ?? ChartTarget, maximumBytes, deadline ?? Now.AddMinutes(1)) };
            Fabric = new(Events);
            Writer = new(Events);
            Coordinator = new(Fabric, Authority, Time);
        }
        public Task<HrReadOutcome> ExecuteAsync(CancellationToken token = default, string rawTarget = Chart)
            => Coordinator.ExecuteAsync("GET", rawTarget, Stream.Null, Writer, token).AsTask();
    }

    private sealed class FixtureTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FixtureAuthority(List<string> events) : IHrReadAuthority
    {
        public HrReadAdmission? Admission { get; set; }
        public int Calls { get; private set; }
        public HrReadTarget? LastTarget { get; private set; }
        public Action? OnResolve { get; set; }
        public Exception? Failure { get; set; }
        public ValueTask<HrReadAdmission?> AuthenticateAndResolveAsync(HrReadTarget target, CancellationToken cancellationToken)
        {
            Calls++; LastTarget = target; events.Add("authority");
            if (Failure is not null) throw Failure;
            OnResolve?.Invoke();
            return ValueTask.FromResult(Admission);
        }
    }

    private sealed class FixtureFabric(List<string> events) : IHrReadFabricAdapter
    {
        public int NonceCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int GateCalls { get; private set; }
        public bool NonceCommitted { get; private set; }
        public bool ReadSnapshotOpen { get; private set; }
        public bool SimulateReplayClaim { get; set; }
        public bool NonceAllows { get; set; } = true;
        public bool GateAllows { get; set; } = true;
        public HrReadAdmission? LastAdmission { get; private set; }
        public string? FailureAt { get; set; }
        public Action? OnNonceCommitted { get; set; }
        public Action? OnGate { get; set; }
        public Func<bool>? GateDecision { get; set; }
        public Func<MaterializedHrRead> ReadResult { get; set; } = () => Detached("{\"fixture\":true}");
        public bool TryClaimNonceInTransaction(HrReadAdmission admission, CancellationToken cancellationToken)
        {
            NonceCalls++; LastAdmission = admission; events.Add("nonce.begin");
            if (FailureAt == "nonce") throw new InvalidOperationException("fixture nonce failure");
            if (!NonceAllows || (SimulateReplayClaim && NonceCommitted)) { events.Add("nonce.rollback"); return false; }
            NonceCommitted = true; events.Add("nonce.commit"); OnNonceCommitted?.Invoke(); return true;
        }
        public MaterializedHrRead ReadInSnapshot(HrReadAdmission admission, CancellationToken cancellationToken)
        {
            ReadCalls++; LastAdmission = admission; events.Add("read.begin"); ReadSnapshotOpen = true;
            try
            {
                if (FailureAt == "read") throw new InvalidOperationException("fixture read failure");
                return ReadResult();
            }
            finally { ReadSnapshotOpen = false; events.Add("read.end"); }
        }
        public bool RevalidateInNewSnapshot(HrReadAdmission admission, CancellationToken cancellationToken)
        {
            GateCalls++; LastAdmission = admission; events.Add("gate.begin");
            try
            {
                if (FailureAt == "gate") throw new InvalidOperationException("fixture gate failure");
                OnGate?.Invoke();
                return GateDecision?.Invoke() ?? GateAllows;
            }
            finally { events.Add("gate.end"); }
        }
    }

    private sealed class FixtureWriter(List<string> events) : IHrReadResponseWriter
    {
        public int Calls { get; private set; }
        public int Status { get; private set; }
        public string? CacheControl { get; private set; }
        public byte[] Bytes { get; private set; } = [];
        public Exception? Failure { get; set; }
        public ValueTask SendAsync(int statusCode, ReadOnlyMemory<byte> bytes, string cacheControl, CancellationToken cancellationToken)
        {
            Calls++; events.Add("send");
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            Status = statusCode; CacheControl = cacheControl; Bytes = bytes.ToArray();
            return ValueTask.CompletedTask;
        }
    }
}
