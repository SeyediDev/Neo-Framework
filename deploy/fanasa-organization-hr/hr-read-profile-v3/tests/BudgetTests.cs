using Fanasa.HrReadCandidate;

internal static class BudgetTests
{
    public static async Task RunAsync()
    {
        var fabric = new NeverFabric(); var writer = new NeverWriter();
        var coordinator = new HrReadCoordinator(fabric, requestBudget: TimeSpan.FromMilliseconds(50));
        using var stalled = new StalledBody();
        await Check.ThrowsAsync<OperationCanceledException>(() => coordinator.ExecuteAsync("GET",
            "/api/organization-hr/v1/tenants/7398590f-2d87-41ce-beda-19fb8e2a45fd/chart", stalled, writer).AsTask(),
            "server execution budget cancels an indefinitely stalled body read");
        Check.True(stalled.ObservedCancellableToken && fabric.Calls == 0 && writer.Calls == 0,
            "timeout needs no client cancellation and cannot enter Fabric or emit payload");
        Check.Throws<ArgumentOutOfRangeException>(() => new HrReadCoordinator(fabric, requestBudget: TimeSpan.Zero), "zero request budget rejected");
        Check.Throws<ArgumentOutOfRangeException>(() => new HrReadCoordinator(fabric, requestBudget: TimeSpan.FromSeconds(6)), "profile execution ceiling enforced");
    }
    private sealed class NeverFabric : IHrReadFabricAdapter
    {
        public int Calls { get; private set; }
        public bool TryClaimNonceInTransaction(HrReadAdmission admission, CancellationToken cancellationToken) { Calls++; throw new Exception("Unexpected Fabric entry."); }
        public MaterializedHrRead ReadInSnapshot(HrReadAdmission admission, CancellationToken cancellationToken) { Calls++; throw new Exception("Unexpected Fabric entry."); }
        public bool RevalidateInNewSnapshot(HrReadAdmission admission, CancellationToken cancellationToken) { Calls++; throw new Exception("Unexpected Fabric entry."); }
    }
    private sealed class NeverWriter : IHrReadResponseWriter
    {
        public int Calls { get; private set; }
        public ValueTask SendAsync(int statusCode, ReadOnlyMemory<byte> bytes, string cacheControl, CancellationToken cancellationToken)
        { Calls++; throw new Exception("Unexpected response."); }
    }
    private sealed class StalledBody : Stream
    {
        public bool ObservedCancellableToken { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ObservedCancellableToken = cancellationToken.CanBeCanceled; await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
