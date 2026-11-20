using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class MessageProgressTests : TestWithLoggingBase
{
    private readonly InMemoryMessageTransport _transport = new();

    public MessageProgressTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task ReportProgressAsync_FromAHandler_AddsThrottledEventsToTheHandlersSpan()
    {
        string name = $"tests.progress-{Guid.NewGuid():N}";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = ListenToHandlerActivities($"handle {name}", stopped);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await MessagingTestHost.StartAsync(_transport, Log, "tests", m => m
            .MapMessage<Work>(name)
            .AddConsumer<Work>(async (_, context, ct) =>
            {
                await context.ReportProgressAsync(10, "started", ct);
                await context.ReportProgressAsync(20, cancellationToken: ct);
                await context.ReportProgressAsync(100, "done", ct);
                handled.TrySetResult();
            }));

        await host.Client.SendAsync(new Work(1), cancellationToken: TestCancellationToken);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        await WaitForAsync(() => !stopped.IsEmpty);

        var events = Assert.Single(stopped).Events.Where(e => e.Name == "progress").ToList();
        Assert.Equal([10, 100], events.Select(e => (int?)e.Tags.Single(t => t.Key == "progress.percent").Value));
        Assert.Equal(["started", "done"], events.Select(e => (string?)e.Tags.Single(t => t.Key == "progress.message").Value));
    }

    [Fact]
    public async Task ReportProgressAsync_WithANullArgument_KeepsThePreviousValue()
    {
        var reported = new TaskCompletionSource<(int?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await MessagingTestHost.StartAsync(_transport, Log, "tests", m => m
            .AddConsumer<Work>(async (_, context, ct) =>
            {
                await context.ReportProgressAsync(40, "copying", ct);
                await context.ReportProgressAsync(message: "verifying", cancellationToken: ct);
                await context.ReportProgressAsync(60, cancellationToken: ct);
                var messageContext = (MessageContext)context;
                reported.TrySetResult((messageContext.Progress, messageContext.ProgressMessage));
            }));

        await host.Client.SendAsync(new Work(1), cancellationToken: TestCancellationToken);

        Assert.Equal((60, "verifying"), await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task ReportProgressAsync_WithAPercentOutOfRange_Throws(int percent)
    {
        var thrown = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await MessagingTestHost.StartAsync(_transport, Log, "tests", m => m
            .AddConsumer<Work>(async (_, context, ct) =>
            {
                try
                {
                    await context.ReportProgressAsync(percent, cancellationToken: ct);
                    thrown.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    thrown.TrySetResult(ex);
                }
            }));

        await host.Client.SendAsync(new Work(1), cancellationToken: TestCancellationToken);

        var exception = Assert.IsType<ArgumentOutOfRangeException>(await thrown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Equal("percent", exception.ParamName);
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private static ActivityListener ListenToHandlerActivities(string operationName, ConcurrentQueue<Activity> stopped)
    {
        // Read the source before adding the listener, so it exists when the listener attaches.
        var foundatio = FoundatioDiagnostics.ActivitySource;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source == foundatio,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == operationName)
                    stopped.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(25, TestCancellationToken);
        }
    }

    public record Work(int Id);
}
