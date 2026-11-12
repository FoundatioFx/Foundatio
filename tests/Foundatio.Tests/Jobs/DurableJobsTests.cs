using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class DurableJobsTests : TestWithLoggingBase
{
    private readonly InMemoryJobStore _store = new();
    private readonly JobProbe _probe = new();

    public DurableJobsTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task EnqueueByArguments_RunsTheJobAndRecordsProgress()
    {
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>());

        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("cat.png", 64), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(100, job.Progress);
        Assert.Equal("resized cat.png to 64px", job.Message);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(typeof(ResizeImage).FullName!.Replace('+', '.'), job.Name);
    }

    [Fact]
    public async Task AProducerWithoutTheJobClass_CanEnqueueByArguments()
    {
        await using var producer = await StartAsync(_ => { });
        await using var worker = await StartAsync(j => j.AddJob<ResizeImageJob>());

        var handle = await producer.Jobs.EnqueueAsync(new ResizeImage("dog.png", 32), cancellationToken: TestCancellationToken);

        Assert.Equal(JobStatus.Succeeded, (await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken)).Status);
    }

    [Fact]
    public async Task ExistingJobBaseJobs_RunUnchanged()
    {
        await using var host = await StartAsync(j => j.AddJob<LegacyCleanupJob>());

        var handle = await host.Jobs.EnqueueAsync<LegacyCleanupJob>(cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal("cleaned up", job.Message);
        Assert.Equal("legacy-cleanup", job.Name);
    }

    [Fact]
    public async Task FailingJob_IsRetriedThenFails()
    {
        await using var host = await StartAsync(j => j.AddJob<FlakyJob>(new JobTypeOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(50)) } }));

        var handle = await host.Jobs.EnqueueAsync(new Flaky(FailTimes: 10), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempt);
        Assert.Contains("failure 3", job.Error);
        Assert.Equal(3, _probe.Runs.Count);
    }

    [Fact]
    public async Task FailingJob_SucceedsWhenARetryWorks()
    {
        await using var host = await StartAsync(j => j.AddJob<FlakyJob>(new JobTypeOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(50)) } }));

        var handle = await host.Jobs.EnqueueAsync(new Flaky(FailTimes: 1), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, job.Attempt);
    }

    [Fact]
    public async Task DoNotRetry_FailsOnTheFirstException()
    {
        await using var host = await StartAsync(j => j.AddJob<FlakyJob>(new JobTypeOptions { RetryPolicy = RetryPolicy.Default.DoNotRetry<InvalidOperationException>() }));

        var job = await (await host.Jobs.EnqueueAsync(new Flaky(FailTimes: 10), cancellationToken: TestCancellationToken)).WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task Cancel_StopsAQueuedJobFromRunning()
    {
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>());
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("later.png", 1), new EnqueueOptions { Delay = TimeSpan.FromMinutes(5) }, TestCancellationToken);

        Assert.True(await handle.CancelAsync(TestCancellationToken));

        Assert.Equal(JobStatus.Cancelled, (await handle.GetAsync(TestCancellationToken))!.Status);
        Assert.False(await handle.CancelAsync(TestCancellationToken));
    }

    [Fact]
    public async Task Cancel_SignalsARunningJobsToken()
    {
        await using var host = await StartAsync(j => j.Configure(o => o.LeaseDuration = TimeSpan.FromSeconds(1.5)).AddJob<WaitForCancellationJob>());
        var handle = await host.Jobs.EnqueueAsync(new WaitForCancellation(), cancellationToken: TestCancellationToken);
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        await handle.CancelAsync(TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task Shutdown_InterruptsRunningJobsWithoutUsingAnAttempt()
    {
        var first = await StartAsync(j => j.Configure(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(100)).AddJob<WaitForCancellationJob>());
        var handle = await first.Jobs.EnqueueAsync(new WaitForCancellation(CompleteOnSecondRun: true), cancellationToken: TestCancellationToken);
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        await first.DisposeAsync();
        var interrupted = (await handle.GetAsync(TestCancellationToken))!;
        Assert.Equal(JobStatus.Queued, interrupted.Status);
        Assert.Equal(0, interrupted.Attempt);

        await using var second = await StartAsync(j => j.AddJob<WaitForCancellationJob>());
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task EnqueueWithAJobId_IsIdempotent()
    {
        await using var host = await StartAsync(_ => { });

        var first = await host.Jobs.EnqueueAsync(new ResizeImage("a.png", 1), new EnqueueOptions { JobId = "resize-a" }, TestCancellationToken);
        var second = await host.Jobs.EnqueueAsync(new ResizeImage("b.png", 2), new EnqueueOptions { JobId = "resize-a" }, TestCancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await host.Jobs.CountAsync(cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task DelayedJob_DoesNotRunEarly()
    {
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>());
        var started = DateTimeOffset.UtcNow;

        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("soon.png", 1), new EnqueueOptions { Delay = TimeSpan.FromSeconds(1) }, TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.True(job.StartedAt - started >= TimeSpan.FromSeconds(0.9), $"Started after {job.StartedAt - started}.");
    }

    [Fact]
    public async Task JobTypeMaxConcurrency_LimitsRunningJobsOfThatType()
    {
        await using var host = await StartAsync(j => j.AddJob<SlowJob>(new JobTypeOptions { MaxConcurrency = 2 }));

        var handles = new List<JobHandle>();
        for (int i = 0; i < 6; i++)
            handles.Add(await host.Jobs.EnqueueAsync(new Slow(), cancellationToken: TestCancellationToken));
        foreach (var handle in handles)
            await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(2, _probe.PeakConcurrency);
    }

    [Fact]
    public async Task Query_ListsJobsNewestFirstWithFilters()
    {
        await using var host = await StartAsync(_ => { });
        for (int i = 0; i < 3; i++)
            await host.Jobs.EnqueueAsync(new ResizeImage($"{i}.png", i), cancellationToken: TestCancellationToken);

        var page = await host.Jobs.QueryAsync(new JobQuery { Status = JobStatus.Queued, Limit = 2 }, TestCancellationToken);

        Assert.Equal(2, page.Jobs.Count);
        Assert.NotNull(page.ContinuationToken);
        Assert.Single((await host.Jobs.QueryAsync(new JobQuery { Status = JobStatus.Queued, ContinuationToken = page.ContinuationToken }, TestCancellationToken)).Jobs);
    }

    [Fact]
    public async Task Cron_CreatesOneJobPerOccurrenceAcrossInstances()
    {
        await using var first = await StartAsync(j => j.AddCronJob<TickJob>("*/5 * * * *", new CronJobOptions { AllowConcurrent = true }), startHostedServices: false);
        await using var second = await StartAsync(j => j.AddCronJob<TickJob>("*/5 * * * *", new CronJobOptions { AllowConcurrent = true }), startHostedServices: false);
        var schedulers = new[] { first.CreateScheduler(), second.CreateScheduler() };
        var midnight = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        foreach (var scheduler in schedulers)
        {
            await scheduler.RunDueAsync(midnight.AddSeconds(1), TestCancellationToken);
            await scheduler.RunDueAsync(midnight.AddMinutes(2), TestCancellationToken);
            await scheduler.RunDueAsync(midnight.AddMinutes(5), TestCancellationToken);
        }

        var jobs = (await first.Jobs.QueryAsync(cancellationToken: TestCancellationToken)).Jobs;
        Assert.Equal(["cron:tick:20260101T000000Z", "cron:tick:20260101T000500Z"], jobs.Select(j => j.Id).Order());
        Assert.All(jobs, j => Assert.Equal("tick", j.ScheduleName));
    }

    [Fact]
    public async Task Cron_WakesAtTheNextOccurrenceOrWithinAMinute()
    {
        await using var host = await StartAsync(j => j.AddCronJob<TickJob>("*/5 * * * *"), startHostedServices: false);
        var midnight = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var scheduler = host.CreateScheduler();

        Assert.Equal(midnight.AddMinutes(5), await scheduler.RunDueAsync(midnight.AddMinutes(4.5), TestCancellationToken));
        Assert.Equal(midnight.AddMinutes(2), await scheduler.RunDueAsync(midnight.AddMinutes(1), TestCancellationToken));
    }

    [Fact]
    public async Task Cron_RunsOnlyTheLatestMissedOccurrenceWithinTheMisfireThreshold()
    {
        await using var host = await StartAsync(j => j
            .AddCronJob<TickJob>("0 * * * *", new CronJobOptions { MisfireThreshold = TimeSpan.FromMinutes(30) })
            .AddCronJob<TickJob>("0 0 * * *", new CronJobOptions { Name = "daily" }), startHostedServices: false);
        var midnight = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await host.CreateScheduler().RunDueAsync(midnight.AddHours(3).AddMinutes(10), TestCancellationToken);

        var jobs = (await host.Jobs.QueryAsync(cancellationToken: TestCancellationToken)).Jobs;
        Assert.Equal(["cron:tick:20260101T030000Z"], jobs.Select(j => j.Id));
    }

    [Fact]
    public async Task Cron_SkipsAnOccurrenceWhileThePreviousOneHasNotFinished()
    {
        await using var host = await StartAsync(j => j.AddCronJob<TickJob>("*/5 * * * *"), startHostedServices: false);
        var scheduler = host.CreateScheduler();
        var midnight = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await scheduler.RunDueAsync(midnight, TestCancellationToken);
        await scheduler.RunDueAsync(midnight.AddMinutes(5), TestCancellationToken);
        await scheduler.RunDueAsync(midnight.AddMinutes(10), TestCancellationToken);
        Assert.Equal(1, await host.Jobs.CountAsync(cancellationToken: TestCancellationToken));

        var first = (await _store.ClaimAsync(new JobClaimRequest { Names = ["tick"], WorkerId = "test", LeaseDuration = TimeSpan.FromMinutes(1) }, TestCancellationToken))!;
        await _store.CompleteAsync(first.Id, first.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromDays(1)), TestCancellationToken);
        await scheduler.RunDueAsync(midnight.AddMinutes(15), TestCancellationToken);
        Assert.Equal(2, await host.Jobs.CountAsync(cancellationToken: TestCancellationToken));

        var restarted = host.CreateScheduler();
        await restarted.RunDueAsync(midnight.AddMinutes(20), TestCancellationToken);
        Assert.Equal(2, await host.Jobs.CountAsync(cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task Cron_WithArguments_EnqueuesTheArgumentsJob()
    {
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>().AddCronJob("* * * * * *", new ResizeImage("nightly.png", 8)));

        await WaitForAsync(async () => await host.Jobs.CountAsync(JobStatus.Succeeded, cancellationToken: TestCancellationToken) > 0);
        var job = (await host.Jobs.QueryAsync(new JobQuery { Status = JobStatus.Succeeded }, TestCancellationToken)).Jobs[0];
        Assert.Equal("resized nightly.png to 8px", job.Message);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WithAFakeTimeProvider_TimesOutInRealTime()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await StartAsync(_ => { }, s => s.AddSingleton<TimeProvider>(time));
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("never.png", 1), cancellationToken: TestCancellationToken);

        var wait = handle.WaitForCompletionAsync(TimeSpan.FromMilliseconds(500), TestCancellationToken);
        var finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(15), TestCancellationToken));

        Assert.Same(wait, finished);
        await Assert.ThrowsAsync<TimeoutException>(() => wait);
    }

    [Fact]
    public async Task EnqueueAsync_ArgumentsTypedAsObject_RunsTheJobForTheirRuntimeType()
    {
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>());
        object arguments = new ResizeImage("boxed.png", 16);

        var handle = await host.Jobs.EnqueueAsync(arguments, cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(typeof(ResizeImage).FullName!.Replace('+', '.'), job.Name);
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task EnqueueAsync_DerivedArgumentsOfANamedBaseType_RunTheJobForTheDerivedType()
    {
        await using var host = await StartAsync(j => j.AddJob<ExportJob>().AddJob<DetailedExportJob>());

        var handle = await host.Jobs.EnqueueAsync<Export>(new DetailedExport("csv", 3), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(typeof(DetailedExport).FullName!.Replace('+', '.'), job.Name);
        Assert.Equal("csv with detail 3", job.Message);
    }

    [Fact]
    public async Task Cron_WithoutConcurrencyOnTwoInstances_DoesNotStartAnOccurrenceWhileAnotherRuns()
    {
        await using var first = await StartAsync(j => j.AddCronJob<TickJob>("0 * * * *"), startHostedServices: false);
        await using var second = await StartAsync(j => j.AddCronJob<TickJob>("0 * * * *"), startHostedServices: false);
        var a = first.CreateScheduler();
        var b = second.CreateScheduler();
        var ten = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var claim = new JobClaimRequest { Names = ["tick"], WorkerId = "test", LeaseDuration = TimeSpan.FromHours(1) };

        await a.RunDueAsync(ten, TestCancellationToken);
        await b.RunDueAsync(ten, TestCancellationToken);
        var tenRun = (await _store.ClaimAsync(claim, TestCancellationToken))!;

        await a.RunDueAsync(ten.AddHours(1), TestCancellationToken);
        await _store.CompleteAsync(tenRun.Id, tenRun.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromDays(1)), TestCancellationToken);
        await b.RunDueAsync(ten.AddHours(1).AddMilliseconds(500), TestCancellationToken);
        var elevenRun = (await _store.ClaimAsync(claim, TestCancellationToken))!;
        Assert.Equal("cron:tick:20260101T110000Z", elevenRun.Id);

        await a.RunDueAsync(ten.AddHours(2), TestCancellationToken);
        await b.RunDueAsync(ten.AddHours(2), TestCancellationToken);

        Assert.Equal(1, await _store.CountAsync(JobStatus.Running, "tick", TestCancellationToken));
        Assert.Equal(0, await _store.CountAsync(JobStatus.Queued, "tick", TestCancellationToken));

        await _store.CompleteAsync(elevenRun.Id, elevenRun.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromDays(1)), TestCancellationToken);
        await a.RunDueAsync(ten.AddHours(3), TestCancellationToken);
        Assert.Equal(1, await _store.CountAsync(JobStatus.Queued, "tick", TestCancellationToken));
    }

    [Fact]
    public void Registration_RejectsMistakesEarlyWithClearErrors()
    {
        var services = new ServiceCollection();

        var cron = Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddCronJob<TickJob>("not cron")));
        Assert.Contains("not a valid CRON expression", cron.Message);
        Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddJob<JobProbe>()));
        Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddCronJob("* * * * *", new TickJob(_probe))));
        Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddCronJob<TickJob>("* * * * *").AddCronJob<TickJob>("0 * * * *")));
        Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddJob<ResizeImageJob>().AddJob<OtherResizeImageJob>()));
        var ignoredName = Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddJob<NamedArgumentsJob>()));
        Assert.Contains("Put the attribute on Slow", ignoredName.Message);

        var noStore = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().GetRequiredService<IJobClient>());
        Assert.Contains("UseInMemory()", noStore.Message);
    }

    [Fact]
    public async Task JobsEnqueuedWhileHandlingAMessage_InheritItsCorrelationId()
    {
        var transport = new InMemoryMessageTransport();
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>(), services => services.AddFoundatioMessaging(m => m
            .UseTransport(transport)
            .AddConsumer<ResizeImage>(async (message, context, ct) => await context.Services.GetRequiredService<IJobClient>().EnqueueAsync(message, cancellationToken: ct))));

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ResizeImage("from-message.png", 1), new SendOptions { CorrelationId = "order-7" }, TestCancellationToken);

        await WaitForAsync(async () => await host.Jobs.CountAsync(JobStatus.Succeeded, cancellationToken: TestCancellationToken) == 1);
        Assert.Equal("order-7", (await host.Jobs.QueryAsync(cancellationToken: TestCancellationToken)).Jobs.Single().CorrelationId);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Workers_WaitForTheStartupGate()
    {
        var gate = new TestStartupGate();
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>(), s => s.AddSingleton<Foundatio.Utility.IStartupGate>(gate));
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("gated.png", 1), cancellationToken: TestCancellationToken);

        await Task.Delay(300, TestCancellationToken);
        Assert.Equal(JobStatus.Queued, (await handle.GetAsync(TestCancellationToken))!.Status);

        gate.Open(true);
        Assert.Equal(JobStatus.Succeeded, (await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken)).Status);
    }

    [Fact]
    public async Task Workers_DoNotStartWhenStartupFails()
    {
        var gate = new TestStartupGate();
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>(), s => s.AddSingleton<Foundatio.Utility.IStartupGate>(gate));
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("never.png", 1), cancellationToken: TestCancellationToken);

        gate.Open(false);
        await Task.Delay(300, TestCancellationToken);

        Assert.Equal(JobStatus.Queued, (await handle.GetAsync(TestCancellationToken))!.Status);
    }

    private sealed class TestStartupGate : Foundatio.Utility.IStartupGate
    {
        private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open(bool success) => _result.TrySetResult(success);

        public Task<bool> WaitAsync(CancellationToken cancellationToken) => _result.Task.WaitAsync(cancellationToken);
    }

    private async Task<TestHost> StartAsync(Action<FoundatioJobsBuilder> configure, Action<IServiceCollection>? services = null, bool startHostedServices = true)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<ILoggerFactory>(Log);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        collection.AddSingleton(_probe);
        services?.Invoke(collection);
        collection.AddFoundatioJobs(j =>
        {
            j.UseStore(_store).Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            configure(j);
        });

        var provider = collection.BuildServiceProvider();
        var hosted = startHostedServices ? provider.GetServices<IHostedService>().ToList() : [];
        foreach (var service in hosted)
            await service.StartAsync(TestCancellationToken);

        return new TestHost(provider, hosted);
    }

    private async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(25, TestCancellationToken);
        }
    }

    private sealed class TestHost(ServiceProvider services, IReadOnlyList<IHostedService> hosted) : IAsyncDisposable
    {
        public IServiceProvider Services => services;
        public IJobClient Jobs => services.GetRequiredService<IJobClient>();

        public JobScheduler CreateScheduler() => new(services.GetRequiredService<JobsRuntime>(), services.GetRequiredService<JobClient>());

        public async ValueTask DisposeAsync()
        {
            foreach (var service in hosted.Reverse())
                await service.StopAsync(CancellationToken.None);

            await services.DisposeAsync();
        }
    }

    public sealed class JobProbe
    {
        private int _running;
        private int _peak;

        public ConcurrentBag<int> Runs { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RunCount;
        public int PeakConcurrency => Volatile.Read(ref _peak);

        public IDisposable Enter()
        {
            int now = Interlocked.Increment(ref _running);
            int peak;
            while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
            {
            }

            return new Exit(this);
        }

        private sealed class Exit(JobProbe probe) : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref probe._running);
        }
    }

    public record ResizeImage(string File, int Width);
    public record Flaky(int FailTimes);
    public record WaitForCancellation(bool CompleteOnSecondRun = false);
    public record Slow;

    [Job(Name = "export")]
    public record Export(string Format);

    public record DetailedExport(string Format, int Detail) : Export(Format);

    public sealed class ExportJob : IJob<Export>
    {
        public Task RunAsync(Export arguments, IJobContext context, CancellationToken cancellationToken) => context.ReportProgressAsync(100, arguments.Format, cancellationToken);
    }

    public sealed class DetailedExportJob : IJob<DetailedExport>
    {
        public Task RunAsync(DetailedExport arguments, IJobContext context, CancellationToken cancellationToken) =>
            context.ReportProgressAsync(100, $"{arguments.Format} with detail {arguments.Detail}", cancellationToken);
    }

    public sealed class ResizeImageJob : IJob<ResizeImage>
    {
        public async Task RunAsync(ResizeImage arguments, IJobContext context, CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(50, "halfway", cancellationToken);
            await context.ReportProgressAsync(100, $"resized {arguments.File} to {arguments.Width}px", cancellationToken);
        }
    }

    public sealed class OtherResizeImageJob : IJob<ResizeImage>
    {
        public Task RunAsync(ResizeImage arguments, IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class FlakyJob(JobProbe probe) : IJob<Flaky>
    {
        public Task RunAsync(Flaky arguments, IJobContext context, CancellationToken cancellationToken)
        {
            probe.Runs.Add(context.Attempt);
            if (context.Attempt <= arguments.FailTimes)
                throw new InvalidOperationException($"failure {context.Attempt}");

            return Task.CompletedTask;
        }
    }

    public sealed class WaitForCancellationJob(JobProbe probe) : IJob<WaitForCancellation>
    {
        public async Task RunAsync(WaitForCancellation arguments, IJobContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref probe.RunCount) > 1 && arguments.CompleteOnSecondRun)
                return;

            probe.Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    [Job(Name = "ignored")]
    public sealed class NamedArgumentsJob : IJob<Slow>
    {
        public Task RunAsync(Slow arguments, IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class SlowJob(JobProbe probe) : IJob<Slow>
    {
        public async Task RunAsync(Slow arguments, IJobContext context, CancellationToken cancellationToken)
        {
            using var _ = probe.Enter();
            await Task.Delay(100, cancellationToken);
        }
    }

    [Job(Name = "tick")]
    public sealed class TickJob(JobProbe probe) : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            probe.Runs.Add(0);
            return Task.FromResult(JobResult.Success);
        }
    }

    [Job(Name = "legacy-cleanup")]
    public sealed class LegacyCleanupJob(ILoggerFactory loggerFactory) : JobBase(loggerFactory)
    {
        protected override Task<JobResult> RunInternalAsync(JobContext context) => Task.FromResult(JobResult.SuccessWithMessage("cleaned up"));
    }
}
