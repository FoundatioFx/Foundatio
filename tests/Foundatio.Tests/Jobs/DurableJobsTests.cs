using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Resilience;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(100, job.Progress);
        Assert.Equal("resized cat.png to 64px", job.Message);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(typeof(ResizeImage).FullName!.Replace('+', '.'), job.Name);
        Assert.Null(job.GetArguments<ResizeImage>());
    }

    [Fact]
    public async Task AProducerWithoutTheJobClass_CanEnqueueByArguments()
    {
        await using var producer = await StartAsync(_ => { });
        await using var worker = await StartAsync(j => j.AddJob<ResizeImageJob>());

        var handle = await producer.Jobs.EnqueueAsync(new ResizeImage("dog.png", 32), cancellationToken: TestCancellationToken);

        Assert.Equal(JobState.Succeeded, (await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken)).State);
    }

    [Fact]
    public async Task EnqueueAsync_ArgumentFreeJob_RunsWithItsContext()
    {
        await using var host = await StartAsync(j => j.AddJob<TickJob>());

        var handle = await host.Jobs.EnqueueAsync<TickJob>(new EnqueueOptions { CorrelationId = "order-3" }, TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal("tick", job.Name);
        Assert.Null(job.GetArguments<ResizeImage>());
        var context = Assert.Single(_probe.Contexts);
        Assert.Equal(handle.Id, context.JobId);
        Assert.Equal("tick", context.JobName);
        Assert.Equal(1, context.Attempt);
        Assert.Equal("order-3", context.CorrelationId);
        Assert.Null(context.ScheduleName);
    }

    [Fact]
    public async Task AddCronJob_ArgumentFreeJob_SeesTheOccurrenceItRunsFor()
    {
        await using var host = await StartAsync(j => j.AddCronJob<TickJob>("0 0 1 1 *"));
        var newYear = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string jobId = "cron:tick:20200101T000000Z";

        await host.CreateScheduler().RunDueAsync(newYear.AddSeconds(1), TestCancellationToken);
        await WaitForAsync(async () => (await host.Jobs.GetAsync(jobId, TestCancellationToken))?.State == JobState.Succeeded);

        var context = Assert.Single(_probe.Contexts, c => c.JobId == jobId);
        Assert.Equal(newYear, context.ScheduledFor);
        Assert.Equal("tick", context.ScheduleName);
        Assert.Equal(1, context.Attempt);
    }

    [Fact]
    public async Task AddLegacyJob_JobBaseJob_RunsUnderItsOwnNameWithItsResultMessage()
    {
        await using var host = await StartAsync(j => j.AddLegacyJob<LegacyCleanupJob>());

        var handle = await host.Jobs.EnqueueAsync<LegacyJob<LegacyCleanupJob>>(cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal("cleaned up", job.Message);
        Assert.Equal("legacy-cleanup", job.Name);
    }

    [Theory]
    [InlineData("failed", "JobFailedException: disk full")]
    [InlineData("exception", "InvalidOperationException: boom")]
    [InlineData("cancelled", "JobFailedException: stopped")]
    public async Task LegacyJob_WithAFailedResult_FailsWithItsErrorOrMessage(string result, string error)
    {
        _probe.LegacyResult = result;
        await using var host = await StartAsync(j => j.AddLegacyJob<ResultLegacyJob>(new JobTypeOptions { RetryPolicy = RetryPolicy.None }));

        var handle = await host.Jobs.EnqueueAsync<LegacyJob<ResultLegacyJob>>(cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(error, job.Error);
    }

    [Fact]
    public async Task LegacyJob_ReturningCancelledAfterCancellation_EndsCancelledWithItsMessage()
    {
        _probe.LegacyResult = "wait";
        await using var host = await StartAsync(j => j.Configure(o => o.LeaseDuration = TimeSpan.FromSeconds(1.5)).AddLegacyJob<ResultLegacyJob>());
        var handle = await host.Jobs.EnqueueAsync<LegacyJob<ResultLegacyJob>>(cancellationToken: TestCancellationToken);
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        await handle.CancelAsync(TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal("stopped early", job.Message);
    }

    [Fact]
    public async Task AddCronJob_OfALegacyJob_SchedulesItUnderTheLegacyJobsName()
    {
        await using var host = await StartAsync(j => j.AddCronJob<LegacyJob<LegacyCleanupJob>>("*/5 * * * *"), startHostedServices: false);

        await host.CreateScheduler().RunDueAsync(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TestCancellationToken);

        var job = Assert.Single((await host.Jobs.QueryAsync(cancellationToken: TestCancellationToken)).Jobs);
        Assert.Equal("cron:legacy-cleanup:20260101T000000Z", job.Id);
        Assert.Equal("legacy-cleanup", job.Name);
    }

    [Fact]
    public async Task EnqueueAsync_WithAJobInsteadOfArguments_ThrowsNamingTheCallToMake()
    {
        await using var host = await StartAsync(_ => { });

        var job = await Assert.ThrowsAsync<ArgumentException>(() => host.Jobs.EnqueueAsync(new TickJob(_probe), cancellationToken: TestCancellationToken));
        var legacy = await Assert.ThrowsAsync<ArgumentException>(() => host.Jobs.EnqueueAsync(new LegacyCleanupJob(Log), cancellationToken: TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Jobs.EnqueueAsync(new ResizeImageJob(), cancellationToken: TestCancellationToken));

        Assert.Contains("EnqueueAsync<TickJob>()", job.Message);
        Assert.Contains("EnqueueAsync<LegacyJob<LegacyCleanupJob>>()", legacy.Message);
    }

    [Fact]
    public async Task FailingJob_IsRetriedThenFails()
    {
        await using var host = await StartAsync(j => j.AddJob<FlakyJob>(new JobTypeOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(50)) } }));

        var handle = await host.Jobs.EnqueueAsync(new Flaky(FailTimes: 10), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(3, job.Attempt);
        Assert.Contains("failure 3", job.Error);
        Assert.Equal(3, _probe.Runs.Count);
        Assert.Equal(new Flaky(FailTimes: 10), job.GetArguments<Flaky>());
    }

    [Fact]
    public async Task FailingJob_SucceedsWhenARetryWorks()
    {
        await using var host = await StartAsync(j => j.AddJob<FlakyJob>(new JobTypeOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(50)) } }));

        var handle = await host.Jobs.EnqueueAsync(new Flaky(FailTimes: 1), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(2, job.Attempt);
    }

    [Fact]
    public async Task DoNotRetry_FailsOnTheFirstException()
    {
        await using var host = await StartAsync(j => j.AddJob<FlakyJob>(new JobTypeOptions { RetryPolicy = RetryPolicy.Default.DoNotRetry<InvalidOperationException>() }));

        var job = await (await host.Jobs.EnqueueAsync(new Flaky(FailTimes: 10), cancellationToken: TestCancellationToken)).WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task Cancel_StopsAQueuedJobFromRunning()
    {
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>());
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("later.png", 1), new EnqueueOptions { Delay = TimeSpan.FromMinutes(5) }, TestCancellationToken);

        Assert.True(await handle.CancelAsync(TestCancellationToken));

        Assert.Equal(JobState.Cancelled, (await handle.GetAsync(TestCancellationToken))!.State);
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

        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task Shutdown_InterruptsRunningJobsWithoutUsingAnAttempt()
    {
        var first = await StartAsync(j => j.Configure(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(100)).AddJob<WaitForCancellationJob>());
        var handle = await first.Jobs.EnqueueAsync(new WaitForCancellation(CompleteOnSecondRun: true), cancellationToken: TestCancellationToken);
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        await first.DisposeAsync();
        var interrupted = (await handle.GetAsync(TestCancellationToken))!;
        Assert.Equal(JobState.Queued, interrupted.State);
        Assert.Equal(0, interrupted.Attempt);

        await using var second = await StartAsync(j => j.AddJob<WaitForCancellationJob>());
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task DisposeServiceProvider_WithoutStoppingTheHost_StopsTheWorker()
    {
        var host = await StartAsync(j => j.AddJob<WaitForCancellationJob>());
        var handle = await host.Jobs.EnqueueAsync(new WaitForCancellation(CompleteOnSecondRun: true), cancellationToken: TestCancellationToken);
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        await ((ServiceProvider)host.Services).DisposeAsync();

        var interrupted = (await handle.GetAsync(TestCancellationToken))!;
        Assert.Equal(JobState.Queued, interrupted.State);
        Assert.Equal(0, interrupted.Attempt);
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
    public async Task GetAsync_OfAQueuedJob_ReturnsItsDetailsAndTypedArgumentsWithoutItsClaim()
    {
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>());
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("later.png", 8), new EnqueueOptions { Delay = TimeSpan.FromMinutes(5), CorrelationId = "order-1" }, TestCancellationToken);

        var details = await handle.GetAsync(TestCancellationToken);
        var listed = Assert.Single((await host.Jobs.QueryAsync(cancellationToken: TestCancellationToken)).Jobs);

        Assert.NotNull(details);
        Assert.Equal(handle.Id, details.Id);
        Assert.Equal(JobState.Queued, details.State);
        Assert.Equal("order-1", details.CorrelationId);
        Assert.Equal(new ResizeImage("later.png", 8), details.GetArguments<ResizeImage>());
        Assert.Equal(new ResizeImage("later.png", 8), listed.GetArguments<ResizeImage>());
        Assert.DoesNotContain(typeof(JobDetails).GetProperties(), p => p.Name is nameof(JobInfo.ClaimToken) or nameof(JobInfo.LeaseExpiresAt) or nameof(JobInfo.Payload));
    }

    [Fact]
    public async Task EnqueueAsync_UnderAnActivity_StoresItsTraceAndRunsTheJobInThatTrace()
    {
        using var listener = ListenToFoundatioActivities();
        await using var host = await StartAsync(j => j.AddJob<TracedJob>());
        using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        request.TraceStateString = "vendor=1";

        var handle = await host.Jobs.EnqueueAsync<TracedJob>(cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(request.Id, job.Headers[KnownHeaders.TraceParent]);
        Assert.Equal("vendor=1", job.Headers[KnownHeaders.TraceState]);
        Assert.Equal(request.Id, job.CorrelationId);
        var run = Assert.Single(_probe.Traces);
        Assert.Equal(request.TraceId, run.TraceId);
        Assert.Equal(request.SpanId, run.ParentSpanId);
        Assert.Equal("vendor=1", run.TraceState);
        Assert.Equal(request.Id, run.Headers[KnownHeaders.TraceParent]);
    }

    [Fact]
    public async Task EnqueueAsync_WithHeaders_KeepsThemAndATraceParentTheCallerSet()
    {
        await using var host = await StartAsync(_ => { });
        using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        request.TraceStateString = "vendor=1";
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("headers.png", 1), new EnqueueOptions
        {
            Delay = TimeSpan.FromMinutes(5),
            Headers = new Dictionary<string, string> { ["tenant"] = "acme", [KnownHeaders.TraceParent] = traceParent }
        }, TestCancellationToken);
        var job = (await handle.GetAsync(TestCancellationToken))!;

        Assert.Equal(new Dictionary<string, string> { ["tenant"] = "acme", [KnownHeaders.TraceParent] = traceParent }, job.Headers);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Jobs.EnqueueAsync(new ResizeImage("bad.png", 1), new EnqueueOptions { Headers = new Dictionary<string, string> { [" "] = "blank" } }, TestCancellationToken));
    }

    [Fact]
    public async Task EnqueueAsync_OutsideAnActivity_StoresNoTraceOrCorrelation()
    {
        await using var host = await StartAsync(_ => { });
        Activity.Current = null;

        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("untraced.png", 1), new EnqueueOptions { Delay = TimeSpan.FromMinutes(5) }, TestCancellationToken);
        var job = (await handle.GetAsync(TestCancellationToken))!;

        Assert.Empty(job.Headers);
        Assert.Null(job.CorrelationId);
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

        var page = await host.Jobs.QueryAsync(new JobQuery { State = JobState.Queued, Limit = 2 }, TestCancellationToken);

        Assert.Equal(2, page.Jobs.Count);
        Assert.NotNull(page.ContinuationToken);
        Assert.Single((await host.Jobs.QueryAsync(new JobQuery { State = JobState.Queued, ContinuationToken = page.ContinuationToken }, TestCancellationToken)).Jobs);
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

        await WaitForAsync(async () => await host.Jobs.CountAsync(JobState.Succeeded, cancellationToken: TestCancellationToken) > 0);
        var job = (await host.Jobs.QueryAsync(new JobQuery { State = JobState.Succeeded }, TestCancellationToken)).Jobs[0];
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
        Assert.Equal(JobState.Succeeded, job.State);
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
    public async Task ReportProgressAsync_WhenTheStoreFailsBriefly_DoesNotFailTheAttempt()
    {
        var store = new FaultyJobStore(_store) { ProgressFailures = 1 };
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>(), store: store);

        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("blip.png", 4), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(100, job.Progress);
        Assert.Equal("resized blip.png to 4px", job.Message);
    }

    [Fact]
    public async Task ReportProgressAsync_AfterAThrottledReport_StoresTheThrottledMessage()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryJobStore(time);
        await store.CreateAsync(new JobInfo { Id = "progress", Name = "progress", CreatedAt = time.GetUtcNow(), UpdatedAt = time.GetUtcNow(), AvailableAt = time.GetUtcNow() }, TestCancellationToken);
        var job = (await store.ClaimAsync(new JobClaimRequest { Names = ["progress"], WorkerId = "test", LeaseDuration = TimeSpan.FromMinutes(1) }, TestCancellationToken))!;
        var context = new JobRunContext(job, store, time, NullLogger.Instance);

        await context.ReportProgressAsync(40, "reading", TestCancellationToken);
        await context.ReportProgressAsync(50, "writing", TestCancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await context.ReportProgressAsync(60, cancellationToken: TestCancellationToken);

        var stored = (await store.GetAsync("progress", TestCancellationToken))!;
        Assert.Equal(60, stored.Progress);
        Assert.Equal("writing", stored.Message);
    }

    [Fact]
    public async Task RunAsync_WhenTheRetryPolicyThrows_FailsTheJob()
    {
        var policy = new RetryPolicy { MaxAttempts = 3, ShouldRetry = _ => throw new InvalidOperationException("policy bug") };
        await using var host = await StartAsync(j => j.AddJob<FlakyJob>(new JobTypeOptions { RetryPolicy = policy }));

        var handle = await host.Jobs.EnqueueAsync(new Flaky(FailTimes: 10), cancellationToken: TestCancellationToken);
        var job = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("policy bug", job.Error);
    }

    [Fact]
    public async Task AddJob_WithLegacySchedulingPropertiesOnJobAttribute_LogsThatTheyAreIgnored()
    {
        await using var host = await StartAsync(j => j.AddLegacyJob<IntervalJob>());

        Assert.Contains(Log.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message.Contains("Interval") && e.Message.Contains(nameof(IntervalJob)));
    }

    [Fact]
    public async Task RunAsync_WhenTheLeaseCannotBeRenewed_CancelsTheJobBeforeAnotherWorkerTakesIt()
    {
        var lease = TimeSpan.FromSeconds(2);
        var partitioned = new FaultyJobStore(_store);
        await using var first = await StartAsync(j => j.Configure(o => { o.LeaseDuration = lease; o.WorkerId = "first"; }).AddJob<OverlapJob>(), store: partitioned);
        var handle = await first.Jobs.EnqueueAsync(new Overlap(), cancellationToken: TestCancellationToken);
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        partitioned.Partitioned = true;
        await using var second = await StartAsync(j => j.Configure(o => { o.LeaseDuration = lease; o.WorkerId = "second"; }).AddJob<OverlapJob>());
        await WaitForAsync(async () => (await _store.GetAsync(handle.Id, TestCancellationToken))?.State.IsTerminal() == true, TimeSpan.FromSeconds(30));
        var job = (await _store.GetAsync(handle.Id, TestCancellationToken))!;

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(2, job.Attempt);
        Assert.Equal(["start 1", "cancelled 1", "start 2"], _probe.Events);
        Assert.Equal(1, _probe.PeakConcurrency);
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

        Assert.Equal(1, await _store.CountAsync(JobState.Running, "tick", TestCancellationToken));
        Assert.Equal(0, await _store.CountAsync(JobState.Queued, "tick", TestCancellationToken));

        await _store.CompleteAsync(elevenRun.Id, elevenRun.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromDays(1)), TestCancellationToken);
        await a.RunDueAsync(ten.AddHours(3), TestCancellationToken);
        Assert.Equal(1, await _store.CountAsync(JobState.Queued, "tick", TestCancellationToken));
    }

    [Fact]
    public void Registration_RejectsMistakesEarlyWithClearErrors()
    {
        var services = new ServiceCollection();

        var cron = Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddCronJob<TickJob>("not cron")));
        Assert.Contains("not a valid CRON expression", cron.Message);
        Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddJob<JobProbe>()));
        Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddCronJob("* * * * *", new TickJob(_probe))));
        var legacyArguments = Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddCronJob("* * * * *", new LegacyCleanupJob(Log))));
        Assert.Contains("AddCronJob<LegacyJob<LegacyCleanupJob>>(cronExpression)", legacyArguments.Message);
        var legacy = Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddJob<LegacyCleanupJob>()));
        Assert.Contains("AddLegacyJob<LegacyCleanupJob>()", legacy.Message);
        Assert.Contains("services.AddJob<LegacyCleanupJob>()", legacy.Message);
        var duplicate = Assert.Throws<ArgumentException>(() => services.AddFoundatioJobs(j => j.AddLegacyJob<LegacyCleanupJob>().AddJob<NamedLikeTheLegacyJob>()));
        Assert.Contains("LegacyJob<LegacyCleanupJob> and NamedLikeTheLegacyJob", duplicate.Message);
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

        await WaitForAsync(async () => await host.Jobs.CountAsync(JobState.Succeeded, cancellationToken: TestCancellationToken) == 1);
        Assert.Equal("order-7", (await host.Jobs.QueryAsync(cancellationToken: TestCancellationToken)).Jobs.Single().CorrelationId);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Workers_WaitForTheStartupGate()
    {
        var gate = new TestStartupGate();
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>(), s => s.AddSingleton<Foundatio.Utility.IStartupGate>(gate));
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("gated.png", 1), cancellationToken: TestCancellationToken);

        await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        Assert.Equal(JobState.Queued, (await handle.GetAsync(TestCancellationToken))!.State);

        gate.Open(true);
        Assert.Equal(JobState.Succeeded, (await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken)).State);
    }

    [Fact]
    public async Task Workers_DoNotStartWhenStartupFails()
    {
        var gate = new TestStartupGate();
        await using var host = await StartAsync(j => j.AddJob<ResizeImageJob>(), s => s.AddSingleton<Foundatio.Utility.IStartupGate>(gate));
        var handle = await host.Jobs.EnqueueAsync(new ResizeImage("never.png", 1), cancellationToken: TestCancellationToken);

        gate.Open(false);
        var status = host.Services.GetRequiredService<JobsStatus>();
        await WaitForAsync(() => Task.FromResult(status.GetComponents().Single().State == Foundatio.Utility.ProcessingState.Faulted));

        Assert.Equal(JobState.Queued, (await handle.GetAsync(TestCancellationToken))!.State);
    }

    private sealed class TestStartupGate : Foundatio.Utility.IStartupGate
    {
        private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open(bool success) => _result.TrySetResult(success);

        public Task<bool> WaitAsync(CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            return _result.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task<TestHost> StartAsync(Action<FoundatioJobsBuilder> configure, Action<IServiceCollection>? services = null, bool startHostedServices = true, IJobStore? store = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<ILoggerFactory>(Log);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        collection.AddSingleton(_probe);
        services?.Invoke(collection);
        collection.AddFoundatioJobs(j =>
        {
            j.UseStore(store ?? _store).Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            configure(j);
        });

        var provider = collection.BuildServiceProvider();
        var hosted = startHostedServices ? provider.GetServices<IHostedService>().ToList() : [];
        foreach (var service in hosted)
            await service.StartAsync(TestCancellationToken);

        return new TestHost(provider, hosted);
    }

    private static ActivityListener ListenToFoundatioActivities()
    {
        // Read the source before adding the listener: initializing it from ShouldListenTo would create it while the
        // listener is being added, and the listener would never attach to it.
        var foundatio = FoundatioDiagnostics.ActivitySource;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source == foundatio,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
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
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<IJobContext> Contexts { get; } = new();
        public ConcurrentQueue<(ActivityTraceId TraceId, ActivitySpanId ParentSpanId, string? TraceState, IReadOnlyDictionary<string, string> Headers)> Traces { get; } = new();
        public string? LegacyResult { get; set; }
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

    public record Overlap;

    [Job(Name = "export")]
    public record Export(string Format);

    public record DetailedExport(string Format, int Detail) : Export(Format);

    public sealed class ExportJob : IJobHandler<Export>
    {
        public Task HandleAsync(Export arguments, IJobContext context, CancellationToken cancellationToken) => context.ReportProgressAsync(100, arguments.Format, cancellationToken);
    }

    public sealed class DetailedExportJob : IJobHandler<DetailedExport>
    {
        public Task HandleAsync(DetailedExport arguments, IJobContext context, CancellationToken cancellationToken) =>
            context.ReportProgressAsync(100, $"{arguments.Format} with detail {arguments.Detail}", cancellationToken);
    }

    /// <summary>Runs until cancelled on its first attempt, recording when it starts and stops; later attempts succeed at once.</summary>
    public sealed class OverlapJob(JobProbe probe) : IJobHandler<Overlap>
    {
        public async Task HandleAsync(Overlap arguments, IJobContext context, CancellationToken cancellationToken)
        {
            using var _ = probe.Enter();
            probe.Events.Enqueue($"start {context.Attempt}");
            if (context.Attempt > 1)
                return;

            probe.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                probe.Events.Enqueue($"cancelled {context.Attempt}");
                throw;
            }
        }
    }

    [Job(Name = "legacy-interval", Interval = "5m")]
    public sealed class IntervalJob : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) => Task.FromResult(JobResult.Success);
    }

    /// <summary>A store that fails on demand: <see cref="ProgressFailures"/> progress reports, or every call once <see cref="Partitioned"/>.</summary>
    private sealed class FaultyJobStore(IJobStore inner) : IJobStore
    {
        private int _progressFailures;
        private int _partitioned;

        public bool Partitioned
        {
            get => Volatile.Read(ref _partitioned) == 1;
            set => Volatile.Write(ref _partitioned, value ? 1 : 0);
        }

        public int ProgressFailures
        {
            get => Volatile.Read(ref _progressFailures);
            init => _progressFailures = value;
        }

        private IJobStore Inner => Partitioned ? throw new TimeoutException("Timeout performing EVALSHA") : inner;

        public Task<bool> CreateAsync(JobInfo job, CancellationToken cancellationToken = default) => Inner.CreateAsync(job, cancellationToken);
        public Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default) => Inner.GetAsync(jobId, cancellationToken);
        public Task<JobPage<JobInfo>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default) => Inner.QueryAsync(query, cancellationToken);
        public Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default) => Inner.CountAsync(state, name, cancellationToken);
        public Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default) => Inner.ClaimAsync(request, cancellationToken);
        public Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Inner.RenewAsync(jobId, claimToken, leaseDuration, cancellationToken);

        public Task<bool> ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _progressFailures) >= 0)
                throw new TimeoutException("Timeout performing EVALSHA");

            return Inner.ReportProgressAsync(jobId, claimToken, percent, message, cancellationToken);
        }

        public Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default) => Inner.CompleteAsync(jobId, claimToken, completion, cancellationToken);
        public Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default) => Inner.RequestCancellationAsync(jobId, retainFor, cancellationToken);
        public Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default) => Inner.DeleteAsync(jobId, cancellationToken);
    }

    public sealed class ResizeImageJob : IJobHandler<ResizeImage>
    {
        public async Task HandleAsync(ResizeImage arguments, IJobContext context, CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(50, "halfway", cancellationToken);
            await context.ReportProgressAsync(100, $"resized {arguments.File} to {arguments.Width}px", cancellationToken);
        }
    }

    public sealed class OtherResizeImageJob : IJobHandler<ResizeImage>
    {
        public Task HandleAsync(ResizeImage arguments, IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class FlakyJob(JobProbe probe) : IJobHandler<Flaky>
    {
        public Task HandleAsync(Flaky arguments, IJobContext context, CancellationToken cancellationToken)
        {
            probe.Runs.Add(context.Attempt);
            if (context.Attempt <= arguments.FailTimes)
                throw new InvalidOperationException($"failure {context.Attempt}");

            return Task.CompletedTask;
        }
    }

    public sealed class WaitForCancellationJob(JobProbe probe) : IJobHandler<WaitForCancellation>
    {
        public async Task HandleAsync(WaitForCancellation arguments, IJobContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref probe.RunCount) > 1 && arguments.CompleteOnSecondRun)
                return;

            probe.Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    [Job(Name = "ignored")]
    public sealed class NamedArgumentsJob : IJobHandler<Slow>
    {
        public Task HandleAsync(Slow arguments, IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class SlowJob(JobProbe probe) : IJobHandler<Slow>
    {
        public async Task HandleAsync(Slow arguments, IJobContext context, CancellationToken cancellationToken)
        {
            using var _ = probe.Enter();
            await Task.Delay(100, cancellationToken);
        }
    }

    [Job(Name = "tick")]
    public sealed class TickJob(JobProbe probe) : IJobHandler
    {
        public Task HandleAsync(IJobContext context, CancellationToken cancellationToken)
        {
            probe.Runs.Add(0);
            probe.Contexts.Enqueue(context);
            return Task.CompletedTask;
        }
    }

    /// <summary>Records the activity it runs in and the headers it was enqueued with.</summary>
    public sealed class TracedJob(JobProbe probe) : IJobHandler
    {
        public Task HandleAsync(IJobContext context, CancellationToken cancellationToken)
        {
            var activity = Activity.Current;
            probe.Traces.Enqueue((activity?.TraceId ?? default, activity?.ParentSpanId ?? default, activity?.TraceStateString, context.Headers));
            return Task.CompletedTask;
        }
    }

    [Job(Name = "legacy-cleanup")]
    public sealed class NamedLikeTheLegacyJob : IJobHandler
    {
        public Task HandleAsync(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Returns the <see cref="JobResult"/> that <see cref="JobProbe.LegacyResult"/> names; "wait" waits to be cancelled.</summary>
    [Job(Name = "legacy-result")]
    public sealed class ResultLegacyJob(JobProbe probe) : IJob
    {
        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            switch (probe.LegacyResult)
            {
                case "failed":
                    return JobResult.FailedWithMessage("disk full");
                case "exception":
                    return JobResult.FromException(new InvalidOperationException("boom"));
                case "cancelled":
                    return JobResult.CancelledWithMessage("stopped");
            }

            probe.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return JobResult.CancelledWithMessage("stopped early");
            }

            return JobResult.Success;
        }
    }

    [Job(Name = "legacy-cleanup")]
    public sealed class LegacyCleanupJob(ILoggerFactory loggerFactory) : JobBase(loggerFactory)
    {
        protected override Task<JobResult> RunInternalAsync(JobContext context) => Task.FromResult(JobResult.SuccessWithMessage("cleaned up"));
    }
}
