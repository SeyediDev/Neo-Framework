using System.Linq.Expressions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MediatR;
using Moq;
using Neo.Application.Behaviours.MediatR;
using Neo.Application.Features.Queue;
using Neo.Infrastructure.Features.Queue.Hangfire;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class QueueDeliveryTests
{
    [Fact]
    public async Task Async_job_serializes_real_method_arguments_and_queue_using_injected_client()
    {
        var client = new Mock<IBackgroundJobClient>();
        Job? captured = null;
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job,IState>((job, _) => captured = job).Returns("job-1");
        var executor = CreateExecutor(client.Object);
        Assert.Equal("job-1", await executor.EnqueueAsync<ISampleJob>((job, token) => job.Run("order-123", token), "outbox", TestContext.Current.CancellationToken));
        Assert.NotNull(captured);
        Assert.Equal(typeof(ISampleJob), captured.Type);
        Assert.Equal(nameof(ISampleJob.Run), captured.Method.Name);
        Assert.Equal("outbox", captured.Queue);
        Assert.Equal("order-123", captured.Args[0]);
        Assert.Equal(CancellationToken.None, captured.Args[1]);
    }
    [Fact]
    public void Continuation_preserves_async_method_instead_of_compiled_delegate()
    {
        var client = new Mock<IBackgroundJobClient>();
        Job? captured = null;
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job,IState>((job, _) => captured = job).Returns("next");
        Assert.Equal("next", CreateExecutor(client.Object).ContinueWith<ISampleJob>("parent", job => job.Run("child", CancellationToken.None), "outbox"));
        Assert.NotNull(captured); Assert.Equal(nameof(ISampleJob.Run), captured.Method.Name); Assert.Equal("child", captured.Args[0]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_enqueue_preserves_unscheduled_events(bool throwError)
    {
        var jobs = new Mock<IJobExecuter>();
        var calls = 0;
        jobs.Setup(x => x.EnqueueAsync(It.IsAny<Expression<Func<IJobPublisher,CancellationToken,Task>>>(), "default", It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? Task.FromResult<string?>("first") : throwError ? Task.FromException<string?>(new InvalidOperationException("offline")) : Task.FromResult<string?>(null));
        var first = new Notice(); var second = new Notice(); var third = new Notice();
        var response = new Response { Events = [first, second, third] };
        var behavior = new PublishEventsOfCommandsBehaviour<Request,Response>(jobs.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(new Request(), _ => Task.FromResult(response), TestContext.Current.CancellationToken));
        Assert.Equal(new INotification[] { second, third }, response.Events);
    }
    private static IJobExecuter CreateExecutor(IBackgroundJobClient client) => (IJobExecuter)Activator.CreateInstance(
        typeof(Neo.Infrastructure.Features.Queue.Hangfire.HangfireServiceCollectionExtensions).Assembly.GetType("Neo.Infrastructure.Features.Queue.Hangfire.HangfireJobExecuter", true)!, client)!;
    public interface ISampleJob : IJob { Task Run(string id, CancellationToken token); }
    private sealed record Request : IRequest<Response>;
    private sealed record Notice : INotification;
    private sealed class Response : IEventContainer
    {
        public List<INotification> Events { get; set; } = [];
        public void AddEvent(INotification notification) => Events.Add(notification);
        public void ClearEvents() => Events.Clear();
    }
}
