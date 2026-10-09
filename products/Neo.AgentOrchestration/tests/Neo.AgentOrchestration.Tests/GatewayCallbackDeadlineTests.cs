using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class GatewayBindingTests
{
    [Fact]
    public async Task Callback_body_timeout_never_acknowledges_or_rewrites_the_frozen_result()
    {
        using var services = Services(); var bindings = services.GetRequiredService<ConfiguredGatewayBindings>();
        var binding = bindings.Resolve("coding", Scope, Profile, Callback);
        using var handler = new StalledCallbackHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        var delivery = new HttpGatewayResultDelivery(client, bindings);
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(TimeSpan.FromSeconds(10));
        const string frozen = "{\"outcome\":\"Failed\",\"summary\":\"unchanged evidence\"}";
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => delivery.DeliverAsync(binding, frozen, safety.Token));
        Assert.True(handler.BodyRead); Assert.False(safety.IsCancellationRequested);
        Assert.Equal("gateway-callback-unavailable", error.Code); Assert.True(error.RequiresReconciliation);
        Assert.Null(error.InnerException); Assert.Equal(1, handler.Requests);
        Assert.Equal(frozen, handler.Payload); Assert.Equal(Callback, handler.Url);
        Assert.Equal(new string('C', 40), handler.Key);
    }

    private sealed class StalledCallbackHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Payload { get; private set; }
        public string? Url { get; private set; }
        public string? Key { get; private set; }
        public bool BodyRead { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; Payload = await request.Content!.ReadAsStringAsync(ct);
            Url = request.RequestUri!.AbsoluteUri; Key = request.Headers.GetValues("X-Neo-Harness-Key").Single();
            return new(HttpStatusCode.OK) { Content = new StreamContent(new StalledReceiptStream(() => BodyRead = true)) };
        }
    }
    private sealed class StalledReceiptStream(Action read) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { read(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
