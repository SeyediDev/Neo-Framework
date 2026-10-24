using System.Text;
using System.Text.Json;
using Fanasa.ContractPolicy;
using Fanasa.HrReadPdp;
using Fanasa.HrReadPdp.Tests;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Fanasa.HrReadHost.Tests;

/// <summary>Actual endpoint/service/central-store execution with explicit isolated authentication and temporary SQLite.</summary>
public static class HostEndpointTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        { count++; if (!value) throw new InvalidOperationException("HR3 host endpoint test failed: " + label); }

        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var preparation = new Preparation(f); var factory = new Factory(f, preparation); var before = f.Fingerprint();
            var sent = await Execute(f, preparation, factory);
            Check(sent && f.Transport.Http.Response.StatusCode == 200, "full endpoint-to-real-central-source path sends a successful PDP response");
            Check(preparation.Calls == 1 && preparation.ObservedOutsideSnapshot && preparation.AsyncCompleted,
                "asynchronous independent preparation completes once before any Fabric read snapshot");
            Check(preparation.ValidCallerReference && factory.Calls == 1 && factory.ReceivedExactAuthentication,
                "new parsed request retains one exact caller proof and passes its prepared source to the host service factory");
            Check(preparation.Source!.Reads > 0 && f.ProofSource.Reads > 0 && preparation.Source.RejectedReferences == 0,
                "the real central source repeatedly validates the prepared caller-bound authority");
            using var response = JsonDocument.Parse(((MemoryStream)f.Transport.Http.Response.Body).ToArray());
            Check(response.RootElement.GetProperty("transportVersion").GetString() == "fanasa.hr-read.pdp.response/1"
                && response.RootElement.GetProperty("requestId").GetString() == f.Transport.Request.RequestId
                && response.RootElement.GetProperty("decision").GetProperty("decision").GetString() == "Permit",
                "serialized actual response binds the request and actual HR3 Permit");
            Check(!response.RootElement.GetProperty("admissionConfirmed").GetBoolean()
                && response.RootElement.GetProperty("decision").GetProperty("obligations").GetArrayLength() > 0,
                "PDP leaves allocation fulfillment explicit instead of claiming admission");
            Check(f.Transport.Http.Response.Headers.CacheControl == "no-store"
                && f.Transport.Http.Response.ContentType == "application/json"
                && f.Transport.Http.Response.ContentLength == f.Transport.Http.Response.Body.Length,
                "complete response has exact bounded JSON length and no-store metadata");
            Check(f.Fingerprint() == before, "full success does not change Fabric, registry, grants, outbox, nonce or ledger");
        }

        foreach (var missing in new[] { "preparation", "factory", "proof" })
        {
            using var f = await CentralHostRecordsTests.CreateFixtureAsync();
            var preparation = new Preparation(f) { ReturnNull = missing == "proof" };
            var factory = new Factory(f, preparation); var before = f.Fingerprint();
            var sent = await Execute(f, missing == "preparation" ? null : preparation, missing == "factory" ? null : factory);
            Check(!sent && f.Transport.Http.Response.StatusCode == 403 && f.Transport.Http.Response.Body.Length == 0
                && f.Transport.Http.Response.Headers.CacheControl == "no-store", "missing " + missing + " denies with an empty no-store response");
            Check(factory.Calls == 0 && f.Fingerprint() == before, "missing " + missing + " cannot create a service, mutate state or fulfill admission");
        }

        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            using var cancellation = new CancellationTokenSource();
            var preparation = new Preparation(f) { BeforePrepare = cancellation.Cancel };
            var factory = new Factory(f, preparation); var before = f.Fingerprint(); var threw = false;
            try { await Execute(f, preparation, factory, cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw && preparation.Calls == 1 && factory.Calls == 0 && preparation.ObservedOutsideSnapshot,
                "cancellation during asynchronous preparation propagates before service/database snapshots");
            Check(f.Fingerprint() == before && f.Transport.Http.Response.Body.Length == 0,
                "preparation cancellation leaves both stores and response bytes untouched");
        }

        foreach (var invalid in new[] { "authentication", "body" })
        {
            using var f = await CentralHostRecordsTests.CreateFixtureAsync();
            var preparation = new Preparation(f); var factory = new Factory(f, preparation); var before = f.Fingerprint();
            if (invalid == "authentication") f.Transport.Http.Request.Headers.Remove("Authorization");
            else { f.Transport.Http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}")); f.Transport.Http.Request.ContentLength = 2; }
            var sent = await Execute(f, preparation, factory);
            Check(!sent && f.Transport.Http.Response.StatusCode == (invalid == "body" ? 400 : 403)
                && preparation.Calls == 0 && factory.Calls == 0 && f.Transport.Http.Response.Body.Length == 0,
                "invalid " + invalid + " is rejected before independent preparation");
            Check(f.Fingerprint() == before, "invalid " + invalid + " cannot alter existing authority/state");
        }

        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var preparation = new Preparation(f) { Throw = new IOException("isolated preparation failure") };
            var factory = new Factory(f, preparation); var before = f.Fingerprint(); var threw = false;
            try { await Execute(f, preparation, factory); } catch (IOException) { threw = true; }
            Check(threw && factory.Calls == 0 && f.Fingerprint() == before && f.Transport.Http.Response.Body.Length == 0,
                "preparation failure propagates without a cached proof or database effect");
        }

        foreach (var partial in new[] { false, true })
        {
            using var f = await CentralHostRecordsTests.CreateFixtureAsync();
            var preparation = new Preparation(f); var factory = new Factory(f, preparation);
            var responseFeature = new ResponseFeature(); f.Transport.Http.Features.Set<IHttpResponseFeature>(responseFeature);
            var output = new FailingWriteStream(partial, () => responseFeature.Started = true);
            f.Transport.Http.Response.Body = output; var before = f.Fingerprint(); var threw = false;
            try { await Execute(f, preparation, factory); } catch (IOException) { threw = true; }
            Check(threw && output.Calls == 1 && f.Transport.Http.Response.StatusCode == 200
                && f.Transport.Http.Response.ContentLength is > 0,
                partial ? "partial send failure propagates without overwriting started status/length" : "initial send failure propagates even before HasStarted");
            Check(output.Length == (partial ? 7 : 0) && f.Transport.Http.Response.HasStarted == partial
                && f.Fingerprint() == before, "send failure never becomes an empty denial or writes authority/accounting state");
        }

        using (var f = new HttpTests.Fixture())
        {
            var clock = new BudgetTime(); f.Http.Request.Body = new PendingReadStream(clock.Fire); f.Http.Request.ContentLength = null;
            var sent = await Hr3HostPdpEndpoint.ExecuteAsync(f.Http, f.CallerSettings, f.Binding,
                certificates: f.Certificates, time: clock);
            Check(!sent && clock.DueTimes.Contains(TimeSpan.FromSeconds(5)) && f.Http.Response.StatusCode == 403
                && f.Http.Response.Body.Length == 0 && f.Authentication.Schemes.Count == 0,
                "whole endpoint budget begins before an uncooperative body read");
        }
        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var clock = new BudgetTime(); var preparation = new Preparation(f) { BeforePrepare = clock.Fire, Pending = true };
            var factory = new Factory(f, preparation, clock); var before = f.Fingerprint();
            var sent = await Execute(f, preparation, factory, clock);
            Check(!sent && preparation.Calls == 1 && factory.Calls == 0 && f.Transport.Http.Response.StatusCode == 403
                && f.Transport.Http.Response.Body.Length == 0 && f.Fingerprint() == before,
                "whole endpoint budget bounds an uncooperative async preparer before Fabric/service access");
        }
        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var clock = new BudgetTime(); var preparation = new Preparation(f); var factory = new Factory(f, preparation, clock);
            var output = new PendingWriteStream(clock.Fire); f.Transport.Http.Response.Body = output;
            var before = f.Fingerprint(); var threw = false;
            try { await Execute(f, preparation, factory, clock); } catch (OperationCanceledException) { threw = true; }
            Check(threw && output.Calls == 1 && !f.Transport.Http.Response.HasStarted
                && f.Transport.Http.Response.StatusCode == 200 && f.Transport.Http.Response.ContentLength is > 0,
                "budget expiration during attempted uncooperative send propagates even before HasStarted");
            Check(f.Fingerprint() == before, "send timeout neither reclassifies admission nor writes state");
        }
        using (var f = new HttpTests.Fixture())
        {
            var feature = new ResponseFeature { Started = true, StatusCode = 202 }; f.Http.Features.Set<IHttpResponseFeature>(feature);
            var threw = false;
            try { await Hr3HostPdpEndpoint.ExecuteAsync(f.Http, f.CallerSettings, f.Binding, certificates: f.Certificates, time: f.Time); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw && feature.StatusCode == 202 && f.Authentication.Schemes.Count == 0,
                "already-started host response remains untouched before body/authentication/preparation");
        }
        return count;
    }

    private static ValueTask<bool> Execute(CentralHostRecordsTests.Fixture f, IHr3AuthenticationPreparer? preparation,
        IHr3HostPdpServiceFactory? factory, TimeProvider? time = null, CancellationToken cancellationToken = default)
        => Hr3HostPdpEndpoint.ExecuteAsync(f.Transport.Http, f.Transport.CallerSettings, f.Transport.Binding,
            preparation, factory, f.Transport.Certificates, time ?? f.Transport.Time, cancellationToken);

    private sealed class Preparation(CentralHostRecordsTests.Fixture fixture) : IHr3AuthenticationPreparer
    {
        public int Calls { get; private set; }
        public bool ReturnNull { get; init; }
        public bool Pending { get; init; }
        public Exception? Throw { get; init; }
        public Action? BeforePrepare { get; init; }
        public bool ObservedOutsideSnapshot { get; private set; }
        public bool ValidCallerReference { get; private set; }
        public bool AsyncCompleted { get; private set; }
        public BoundSource? Source { get; private set; }
        public ValueTask<IHr3RetainedAuthenticationSource?> PrepareAsync(HrReadPdpRequest request,
            AuthenticatedHrReadPdpCaller caller, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            ObservedOutsideSnapshot = !fixture.Transport.Database.IsInTransaction;
            ValidCallerReference = caller.IsFor(request) && ReferenceEquals(caller.RequestContext, fixture.Transport.Http)
                && !ReferenceEquals(request, fixture.Transport.Request);
            if (!ObservedOutsideSnapshot || !ValidCallerReference) throw new InvalidOperationException("Preparation requires the exact native endpoint proof outside Fabric.");
            BeforePrepare?.Invoke();
            if (Throw is not null) throw Throw;
            if (Pending) return new(new TaskCompletionSource<IHr3RetainedAuthenticationSource?>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
            return CompleteAsync(caller, ct);
        }
        private async ValueTask<IHr3RetainedAuthenticationSource?> CompleteAsync(AuthenticatedHrReadPdpCaller caller, CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); AsyncCompleted = true;
            return ReturnNull ? null : Source = new BoundSource(fixture.ProofSource, caller);
        }
    }
    private sealed class BoundSource(IHr3RetainedAuthenticationSource inner, AuthenticatedHrReadPdpCaller expected) : IHr3RetainedAuthenticationSource
    {
        public int Reads { get; private set; }
        public int RejectedReferences { get; private set; }
        public Hr3RetainedAuthenticationProof? ReadCurrent(TrustedAccessSnapshotQuery query, AuthenticatedHrReadPdpCaller caller, CancellationToken ct)
        {
            Reads++; if (!ReferenceEquals(expected, caller)) { RejectedReferences++; return null; }
            return inner.ReadCurrent(query, caller, ct);
        }
    }
    private sealed class Factory(CentralHostRecordsTests.Fixture fixture, Preparation preparation, TimeProvider? time = null) : IHr3HostPdpServiceFactory
    {
        public int Calls { get; private set; }
        public bool ReceivedExactAuthentication { get; private set; }
        public HrReadPdpService Create(IHr3RetainedAuthenticationSource authentication)
        {
            Calls++; ReceivedExactAuthentication = ReferenceEquals(authentication, preparation.Source);
            if (!ReceivedExactAuthentication || fixture.Transport.Database.IsInTransaction) throw new InvalidOperationException("Host factory received another or unprepared authentication source.");
            return new ExistingHostHr3PdpServiceFactory(fixture.Transport.Database, fixture.Registry, fixture.Access,
                fixture.Legacy, fixture.Transport.Module, time ?? fixture.Transport.Time).Create(authentication);
        }
    }
    private sealed class BudgetTime : TimeProvider
    {
        private readonly List<Action> callbacks = [];
        public List<TimeSpan> DueTimes { get; } = [];
        public override DateTimeOffset GetUtcNow() => HttpTests.Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { callbacks.Add(() => callback(state)); DueTimes.Add(dueTime); return new Timer(); }
        public void Fire() { var pending = callbacks.ToArray(); callbacks.Clear(); foreach (var callback in pending) callback(); }
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class PendingReadStream(Action onRead) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken ct = default)
        { onRead(); return new(new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task); }
    }
    private sealed class PendingWriteStream(Action onWrite) : MemoryStream
    {
        public int Calls { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        { Calls++; onWrite(); return new(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task); }
    }
    private sealed class FailingWriteStream(bool partial, Action started) : MemoryStream
    {
        public int Calls { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            Calls++; ct.ThrowIfCancellationRequested();
            if (partial) { Write(bytes.Span[..7]); started(); }
            return ValueTask.FromException(new IOException("isolated response write failure"));
        }
    }
    private sealed class ResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; }
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool Started { get; set; }
        public bool HasStarted => Started;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
