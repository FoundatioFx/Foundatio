using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Hosting;

public class ShutdownTests : TestWithLoggingBase
{
    private static readonly TimeSpan WorkDuration = TimeSpan.FromSeconds(2);

    private readonly InMemoryMessageTransport _transport = new();
    private readonly InMemoryJobStore _store = new();
    private readonly Work _work = new();

    public ShutdownTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task HostStop_WhileAJobDrains_DoesNotHandleNewMessages()
    {
        using var host = BuildHost();
        await host.StartAsync(TestCancellationToken);
        await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new SlowJobArguments(), cancellationToken: TestCancellationToken);
        await _work.JobStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        var stopping = host.StopAsync(TestCancellationToken);
        await Task.Delay(300, TestCancellationToken);
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SlowCommand(), cancellationToken: TestCancellationToken);
        await stopping;

        Assert.Equal(1, Volatile.Read(ref _work.JobsFinished));
        Assert.Equal(0, Volatile.Read(ref _work.MessagesStarted));
    }

    [Fact]
    public async Task HostStop_WithMessagesAndJobsOutlastingTheirTimeouts_DrainsThemTogether()
    {
        using var host = BuildHost(shutdownTimeout: TimeSpan.FromSeconds(1), untilCancelled: true);
        await host.StartAsync(TestCancellationToken);
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SlowCommand(), cancellationToken: TestCancellationToken);
        await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new SlowJobArguments(), cancellationToken: TestCancellationToken);
        await Task.WhenAll(_work.MessageStarted.Task, _work.JobStarted.Task).WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(TestCancellationToken);
        stopwatch.Stop();

        Assert.Equal(1, Volatile.Read(ref _work.MessagesFinished));
        Assert.Equal(1, Volatile.Read(ref _work.JobsFinished));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.75), $"Stopping took {stopwatch.Elapsed}; draining one after the other takes the sum of both timeouts, 2 s.");
    }

    /// <summary>Messaging is registered first, so the host stops the job worker first.</summary>
    private IHost BuildHost(TimeSpan? shutdownTimeout = null, bool untilCancelled = false) => new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerFactory>(Log);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            _work.UntilCancelled = untilCancelled;
            services.AddSingleton(_work);
            services.AddFoundatioMessaging(m => m.UseTransport(_transport).UseServiceName("tests")
                .Configure(o => o.ShutdownTimeout = shutdownTimeout ?? o.ShutdownTimeout)
                .AddConsumer<SlowCommand>(async (_, _, cancellationToken) =>
                {
                    Interlocked.Increment(ref _work.MessagesStarted);
                    _work.MessageStarted.TrySetResult();
                    await _work.WaitAsync(cancellationToken);
                    Interlocked.Increment(ref _work.MessagesFinished);
                }));
            services.AddFoundatioJobs(j => j.UseStore(_store)
                .Configure(o => o.ShutdownTimeout = shutdownTimeout ?? o.ShutdownTimeout)
                .AddJob<SlowJob>());
        })
        .Build();

    public record SlowCommand;
    public record SlowJobArguments;

    public sealed class Work
    {
        public int MessagesStarted;
        public int MessagesFinished;
        public int JobsFinished;
        public TaskCompletionSource MessageStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource JobStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool UntilCancelled { get; set; }

        /// <summary>Works for <see cref="WorkDuration"/>, ignoring cancellation, or until cancelled when <see cref="UntilCancelled"/> is set.</summary>
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (!UntilCancelled)
            {
                await Task.Delay(WorkDuration, CancellationToken.None);
                return;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public sealed class SlowJob(Work work) : IJobHandler<SlowJobArguments>
    {
        public async Task HandleAsync(SlowJobArguments arguments, IJobContext context, CancellationToken cancellationToken)
        {
            work.JobStarted.TrySetResult();
            await work.WaitAsync(cancellationToken);
            Interlocked.Increment(ref work.JobsFinished);
        }
    }
}
