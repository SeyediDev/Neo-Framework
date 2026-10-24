using System.Security.Cryptography;
using System.Text;

namespace Fanasa.HrReadCandidate;

internal static class WireTests
{
    private const string Tenant = "7398590f-2d87-41ce-beda-19fb8e2a45fd";
    private const string Chart = "/api/organization-hr/v1/tenants/" + Tenant + "/chart";
    private const string Authorization = "Bearer fixture-token";
    private const string Envelope = "eyJmaXh0dXJlIjp0cnVlfQ==";
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    public static void Run()
    {
        Check.True(HrReadWire.TryParseTarget("GET", Chart, out var chart)
            && chart == new HrReadTarget(HrReadEndpointKind.Chart, Guid.Parse(Tenant), null), "canonical chart target");
        Check.True(HrReadWire.TryParseTarget("GET", Chart.Replace("/chart", "/history/9223372036854775807"), out var history)
            && history == new HrReadTarget(HrReadEndpointKind.History, Guid.Parse(Tenant), long.MaxValue), "canonical maximum Int64 history revision matches backend signature");
        Check.True(HrReadWire.TryParseTarget("GET", Chart.Replace("/chart", "/history/2147483648"), out _), "history revision above Int32 is supported by source597");
        string[] invalidTargets = [Chart + "?x=1", Chart + "#fragment", Chart + "/", Chart.Replace("/chart", "//chart"),
            Chart.Replace(Tenant, Tenant.ToUpperInvariant()), Chart.Replace(Tenant, Guid.Empty.ToString("D")),
            Chart.Replace("739", "%37739"), Chart.Replace("/chart", "/history/0"), Chart.Replace("/chart", "/history/01"),
            Chart.Replace("/chart", "/history/+1"), Chart.Replace("/chart", "/history/-1"), Chart.Replace("/chart", "/history/1.0"),
            Chart.Replace("/chart", "/history/9223372036854775808"), Chart.Replace("/chart", "/history/1/"),
            Chart.Replace("/chart", "/commands"), Chart.Replace("/chart", "/../chart"), Chart + "\ud800", "https://example.test" + Chart];
        foreach (var invalid in invalidTargets)
            Check.True(!HrReadWire.TryParseTarget("GET", invalid, out var target) && target is null, "reject noncanonical target");
        foreach (var method in new[] { "get", "POST", "HEAD", "GET ", "" })
            Check.True(!HrReadWire.TryParseTarget(method, Chart, out _), "reject unsupported method");
        Check.True(!HrReadWire.TryParseTarget("GET", null!, out _), "reject null raw target");

        var frame = HrReadWire.BuildFrame("GET", Chart, Authorization, Envelope, "", []);
        // Independent known answer calculated with Python hashlib/hmac and explicit byte-length framing.
        const string expectedFrame = "17:fanasa.context.v13:GET74:/api/organization-hr/v1/tenants/7398590f-2d87-41ce-beda-19fb8e2a45fd/chart"
            + "64:8f9a659844498bfc0737d9f65ab350228e62e2f61e2e5fd08c39e4d0fb933c57"
            + "64:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            + "64:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            + "24:eyJmaXh0dXJlIjp0cnVlfQ==";
        Check.True(frame.Length == 330 && Encoding.UTF8.GetString(frame) == expectedFrame, "exact seven-part source597 frame");
        Check.True(Convert.ToHexString(SHA256.HashData(frame)).ToLowerInvariant()
            == "368a12a4368792796b7ccf9bbdfcdf2bc33669952e2240a44f31a9d1e988d7c0", "independent frame SHA256");
        var signature = HrReadWire.Sign(frame, Key);
        Check.True(Convert.ToHexString(signature).ToLowerInvariant()
            == "0e406b837b7ea8d28371804163d63398d712c9278830b2b251c1fe8f9b4ccdf1", "independent HMAC SHA256");
        Check.True(HrReadWire.Verify(frame, Key, signature), "valid signature primitive");
        byte[][] changed = [HrReadWire.BuildFrame("POST", Chart, Authorization, Envelope, "", []),
            HrReadWire.BuildFrame("GET", Chart + "?x=1", Authorization, Envelope, "", []),
            HrReadWire.BuildFrame("GET", Chart, Authorization + " ", Envelope, "", []),
            HrReadWire.BuildFrame("GET", Chart, Authorization, Envelope, "", [0]),
            HrReadWire.BuildFrame("GET", Chart, Authorization, Envelope, "fixture-id", []),
            HrReadWire.BuildFrame("GET", Chart, Authorization, "eyJmaXh0dXJlIjpmYWxzZX0=", "", [])];
        foreach (var altered in changed) Check.True(!HrReadWire.Verify(altered, Key, signature), "every signed component is bound");
        var changedDomain = frame.ToArray();
        changedDomain[3] ^= 1;
        Check.True(!HrReadWire.Verify(changedDomain, Key, signature), "domain separator is bound");
        var wrongKey = Key.ToArray();
        wrongKey[0] ^= 1;
        Check.True(!HrReadWire.Verify(frame, wrongKey, signature), "wrong key rejected");
        Check.True(!HrReadWire.Verify(frame, Key, signature[..31]), "wrong signature size rejected");
        Check.True(!HrReadWire.Verify([], Key, signature), "empty frame rejected");
        Check.True(!HrReadWire.Verify(frame, new byte[31], signature), "short key rejected");

        Check.Throws<EncoderFallbackException>(() => HrReadWire.BuildFrame("GET", Chart, "\ud800", Envelope, "", []), "unpaired surrogate rejected");
        Check.Throws<ArgumentOutOfRangeException>(() => HrReadWire.BuildFrame("GET", Chart, Authorization,
            Envelope, new string('a', HrReadWire.MaximumIdempotencyBytes + 1), []), "idempotency allocation cap");
        Check.Throws<ArgumentOutOfRangeException>(() => HrReadWire.BuildFrame("GET", Chart, Authorization,
            new string('ا', HrReadWire.MaximumEnvelopeBytes / 2 + 1), "", []), "byte cap applies to UTF8 rather than character count");
        Check.Throws<ArgumentOutOfRangeException>(() => HrReadWire.BuildFrame("GET", Chart, Authorization,
            Envelope, "", new byte[HrReadWire.MaximumBodyBytes + 1]), "body allocation cap");
        Check.Throws<ArgumentOutOfRangeException>(() => HrReadWire.Sign(frame, new byte[31]), "short signing key rejected");
    }

    public static async Task RunAsync()
    {
        using var empty = new NonSeekableStream([]);
        using var nonempty = new NonSeekableStream([1, 2]);
        Check.True(await HrReadWire.IsActuallyEmptyAsync(empty, CancellationToken.None), "actual empty nonseekable body");
        Check.True(!await HrReadWire.IsActuallyEmptyAsync(nonempty, CancellationToken.None), "nonempty body rejected without declared length");
        Check.True(nonempty.BytesRead == 1, "empty-body check consumes at most one byte");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var observedCancellation = false;
        try { await HrReadWire.IsActuallyEmptyAsync(empty, cancelled.Token); }
        catch (OperationCanceledException) { observedCancellation = true; }
        Check.True(observedCancellation, "preexisting cancellation closes empty-body path");
        using var during = new CancellationTokenSource();
        using var ignoresCancellation = new NonSeekableStream([], during.Cancel);
        observedCancellation = false;
        try { await HrReadWire.IsActuallyEmptyAsync(ignoresCancellation, during.Token); }
        catch (OperationCanceledException) { observedCancellation = true; }
        Check.True(observedCancellation, "cancellation during stream read is observed even when stream ignores token");
    }

    private sealed class NonSeekableStream(byte[] bytes, Action? onRead = null) : Stream
    {
        private int position;
        public int BytesRead => position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead?.Invoke();
            var count = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
