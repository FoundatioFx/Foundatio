using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Monitoring;
using Foundatio.Resilience;
using Foundatio.Tests.Messaging;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Monitoring;

public class WorkTrackingTests : TestWithLoggingBase
{
    private static readonly RetryPolicy FastRetries = new() { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(10)) };

    private readonly InMemoryMessageTransport _transport = new();

    public WorkTrackingTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Delivery_WhileTheHandlerReportsProgress_IsActiveThenRecent()
    {
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await MessagingTestHost.StartAsync(_transport, Log, "tests", m => m
            .MapMessage<ImportCatalog>("tests.import-catalog")
            .AddConsumer<ImportCatalog>(async (_, context, ct) =>
            {
                await context.ReportProgressAsync(50, "half way", ct);
                reported.TrySetResult();
                await release.Task.WaitAsync(ct);
            }));
        var tracker = host.Services.GetRequiredService<WorkTracker>();

        await host.Client.SendAsync(new ImportCatalog("spring"), new SendOptions { CorrelationId = "import-1" }, TestCancellationToken);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        var active = Assert.Single(tracker.GetSnapshot().Active);
        Assert.Equal(WorkKind.Message, active.Kind);
        Assert.Equal("tests.import-catalog", active.Name);
        Assert.Equal("queue:tests.import-catalog", active.Source);
        Assert.Equal("ImportCatalog delegate", active.Handler);
        Assert.Equal("import-1", active.CorrelationId);
        Assert.Equal(1, active.Attempt);
        Assert.Equal(50, active.Progress);
        Assert.Equal("half way", active.ProgressMessage);
        Assert.Equal(1, Assert.Single(tracker.GetSnapshot().Stats).InFlight);

        release.TrySetResult();
        await WaitForAsync(() => tracker.GetSnapshot().Recent.Count > 0);

        var snapshot = tracker.GetSnapshot();
        Assert.Empty(snapshot.Active);
        var recent = Assert.Single(snapshot.Recent);
        Assert.Equal((active.Id, WorkOutcome.Succeeded, 50, "half way"), (recent.Id, recent.Outcome, recent.Progress, recent.ProgressMessage));
        var stats = Assert.Single(snapshot.Stats);
        Assert.Equal((1, 0), (stats.Succeeded, stats.InFlight));
    }

    [Fact]
    public async Task Delivery_WhenTheHandlerKeepsFailing_CountsEachRetryAndTheDeadLetter()
    {
        await using var host = await MessagingTestHost.StartAsync(_transport, Log, "tests", m => m
            .MapMessage<ImportCatalog>("tests.import-catalog")
            .AddConsumer<ImportCatalog>((_, context, _) => throw new InvalidOperationException($"boom {context.Attempt}"), new HandlerOptions { RetryPolicy = FastRetries }));
        var tracker = host.Services.GetRequiredService<WorkTracker>();

        await host.Client.SendAsync(new ImportCatalog("broken"), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => tracker.GetSnapshot().Stats.Any(s => s.DeadLettered == 1));

        var snapshot = tracker.GetSnapshot();
        var stats = Assert.Single(snapshot.Stats);
        Assert.Equal((0, 2, 1, 3), (stats.Succeeded, stats.Retried, stats.DeadLettered, stats.Timed));
        Assert.Equal([WorkOutcome.DeadLettered, WorkOutcome.Retried, WorkOutcome.Retried], snapshot.Recent.Select(r => r.Outcome));
        Assert.Equal([3, 2, 1], snapshot.Recent.Select(r => r.Attempt));
        Assert.Equal("InvalidOperationException: boom 3", snapshot.Recent[0].Error);
    }

    [Fact]
    public async Task Delivery_OfATypeASubscriptionHasNoHandlerFor_IsCountedAsSkipped()
    {
        await using var host = await MessagingTestHost.StartAsync(_transport, Log, "tests", m => m
            .MapMessage<CatalogImported>("tests.catalog-imported", topic: "tests.catalog")
            .MapMessage<CatalogDeleted>("tests.catalog-deleted", topic: "tests.catalog")
            .AddSubscriber<CatalogImported>((_, _, _) => Task.CompletedTask));
        var tracker = host.Services.GetRequiredService<WorkTracker>();

        await host.Client.PublishAsync(new CatalogDeleted("winter"), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new CatalogImported("spring"), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => tracker.GetSnapshot().Stats.Sum(s => s.Succeeded + s.Skipped) == 2);

        var stats = tracker.GetSnapshot().Stats.OrderBy(s => s.Name).ToList();
        Assert.Equal(["tests.catalog-deleted", "tests.catalog-imported"], stats.Select(s => s.Name));
        Assert.All(stats, s => Assert.Equal("subscription:tests.catalog/tests", s.Source));
        Assert.Equal((1, 0), (stats[0].Skipped, stats[0].Timed));
        Assert.Equal((1, 1), (stats[1].Succeeded, stats[1].Timed));
        Assert.Empty(tracker.GetSnapshot().Recent);
    }

    [Fact]
    public async Task JobRun_WhileItReportsProgress_IsActiveThenRecent()
    {
        var probe = new JobProbe();
        await using var host = await StartJobsAsync(probe);
        var tracker = host.Services.GetRequiredService<WorkTracker>();

        var handle = await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new Rebuild("index"), new EnqueueOptions { CorrelationId = "rebuild-1" }, TestCancellationToken);
        await probe.Reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        var active = Assert.Single(tracker.GetSnapshot().Active);
        Assert.Equal((WorkKind.Job, handle.Id, "tests.rebuild", null), (active.Kind, active.Id, active.Name, active.Source));
        Assert.Equal("RebuildJob", active.Handler);
        Assert.Equal("rebuild-1", active.CorrelationId);
        Assert.Equal((10, "rebuilding index"), (active.Progress, active.ProgressMessage));

        probe.Release.TrySetResult();
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.Equal(JobState.Succeeded, job.State);
        await WaitForAsync(() => tracker.GetSnapshot().Recent.Count > 0);

        var recent = Assert.Single(tracker.GetSnapshot().Recent);
        Assert.Equal((handle.Id, WorkOutcome.Succeeded, 100), (recent.Id, recent.Outcome, recent.Progress));
        Assert.Equal(1, Assert.Single(tracker.GetSnapshot().Stats).Succeeded);
    }

    [Fact]
    public async Task JobRun_WhenTheJobKeepsFailing_CountsEachRetryAndTheFailure()
    {
        var probe = new JobProbe { Fail = true };
        probe.Release.TrySetResult();
        await using var host = await StartJobsAsync(probe);
        var tracker = host.Services.GetRequiredService<WorkTracker>();

        var handle = await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new Rebuild("index"), cancellationToken: TestCancellationToken);
        Assert.Equal(JobState.Failed, (await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken)).State);
        await WaitForAsync(() => tracker.GetSnapshot().Stats.Any(s => s.Failed == 1));

        var stats = Assert.Single(tracker.GetSnapshot().Stats);
        Assert.Equal((2, 1, 3), (stats.Retried, stats.Failed, stats.Timed));
        var recent = tracker.GetSnapshot().Recent;
        Assert.Equal([WorkOutcome.Failed, WorkOutcome.Retried, WorkOutcome.Retried], recent.Select(r => r.Outcome));
        Assert.All(recent, r => Assert.Equal("InvalidOperationException: the index is corrupt", r.Error));
    }

    [Fact]
    public async Task JobRun_WhenCancelledBeforeItStarts_IsCountedAsCancelled()
    {
        var probe = new JobProbe();
        await using var host = await StartJobsAsync(probe, startWorker: false);
        var jobs = host.Services.GetRequiredService<IJobClient>();
        var handle = await jobs.EnqueueAsync(new Rebuild("index"), cancellationToken: TestCancellationToken);
        var store = host.Services.GetRequiredService<IJobStore>();
        var tracker = host.Services.GetRequiredService<WorkTracker>();
        var claimed = await store.ClaimAsync(new JobClaimRequest { Names = ["tests.rebuild"], WorkerId = "other", LeaseDuration = TimeSpan.FromMilliseconds(200) }, TestCancellationToken);
        Assert.NotNull(claimed);
        Assert.True(await jobs.CancelAsync(handle.Id, TestCancellationToken));

        await host.StartAsync();
        Assert.Equal(JobState.Cancelled, (await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken)).State);
        await WaitForAsync(() => tracker.GetSnapshot().Stats.Any(s => s.Cancelled == 1));

        Assert.Equal(WorkOutcome.Cancelled, Assert.Single(tracker.GetSnapshot().Recent).Outcome);
        Assert.False(probe.Reported.Task.IsCompleted);
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<JobsHost> StartJobsAsync(JobProbe probe, bool startWorker = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(probe);
        services.AddFoundatioJobs(j => j
            .UseInMemory()
            .Configure(o =>
            {
                o.DefaultRetryPolicy = FastRetries;
                o.ShutdownTimeout = TimeSpan.FromSeconds(5);
                o.LeaseDuration = TimeSpan.FromSeconds(1);
            })
            .AddJob<RebuildJob>());

        var host = new JobsHost(services.BuildServiceProvider());
        if (startWorker)
            await host.StartAsync();

        return host;
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

    public record ImportCatalog(string Season);

    public record CatalogImported(string Season);

    public record CatalogDeleted(string Season);

    [Job(Name = "tests.rebuild")]
    public record Rebuild(string Index);

    public sealed class JobProbe
    {
        public bool Fail { get; init; }
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class RebuildJob(JobProbe probe) : IJobHandler<Rebuild>
    {
        public async Task HandleAsync(Rebuild arguments, IJobContext context, CancellationToken cancellationToken)
        {
            if (probe.Fail)
                throw new InvalidOperationException($"the {arguments.Index} is corrupt");

            await context.ReportProgressAsync(10, $"rebuilding {arguments.Index}", cancellationToken);
            probe.Reported.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
            await context.ReportProgressAsync(100, cancellationToken: cancellationToken);
        }
    }

    private sealed class JobsHost(ServiceProvider services) : IAsyncDisposable
    {
        private readonly List<IHostedService> _started = [];

        public IServiceProvider Services => services;

        public async Task StartAsync()
        {
            foreach (var service in services.GetServices<IHostedService>())
            {
                await service.StartAsync(CancellationToken.None);
                _started.Add(service);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var service in Enumerable.Reverse(_started))
                await service.StopAsync(CancellationToken.None);

            await services.DisposeAsync();
        }
    }
}
