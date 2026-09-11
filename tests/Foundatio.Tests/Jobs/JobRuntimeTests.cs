using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class JobRuntimeTests
{
    private static JobTypeRegistry CreateJobRegistry() => new(typeof(JobRuntimeTests).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
        .Where(t => t.IsClass && !t.IsAbstract && typeof(IJob).IsAssignableFrom(t))
        .Select(t => new JobTypeRegistration(t.FullName!, t)));

    [Fact]
    public async Task CreateIfAbsentAsync_AtCapacity_PreservesExistingWorkAndRejectsNewWorkAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore(maxJobs: 1);
        var state = new JobState { JobId = "one", Name = "work" };
        await store.CreateIfAbsentAsync(state, token);
        await store.CreateIfAbsentAsync(state, token);
        await Assert.ThrowsAsync<JobException>(() => store.CreateIfAbsentAsync(state with { JobId = "two" }, token));
        Assert.Equal("one", Assert.Single(await store.QueryAsync(new JobQuery(), token)).JobId);
    }

    [Fact]
    public async Task RequestCancellationAsync_BeforeClaim_PreventsExecutionAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        await using var provider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var client = new JobClient(store);
        var handle = await client.EnqueueAsync<SuccessfulTrackedJob>(cancellationToken: token);
        await handle.RequestCancellationAsync(token);
        var worker = new JobWorker(store, provider, new JobWorkerOptions { JobTypes = CreateJobRegistry() });
        Assert.False(await worker.RunAsync(handle.JobId, token));
        var state = await handle.GetStateAsync(token);
        Assert.Equal(JobStatus.Cancelled, state!.Status);
        Assert.Equal(0, state.Attempt);
    }

    [Fact]
    public async Task RunAsync_HostStops_LeavesUnfinishedWorkQueuedAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection().AddSingleton(started).BuildServiceProvider();
        var client = new JobClient(store);
        var handle = await client.EnqueueAsync<InterruptedJob>(cancellationToken: token);
        var worker = new JobWorker(store, provider, new JobWorkerOptions { JobTypes = CreateJobRegistry() });
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
        var run = worker.RunAsync(handle.JobId, shutdown.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await shutdown.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), token);
        var state = await handle.GetStateAsync(token);
        Assert.Equal(JobStatus.Queued, state!.Status);
        Assert.Null(state.CompletedUtc);
        Assert.False(state.CancellationRequested);
    }

    private sealed class InterruptedJob(TaskCompletionSource started) : IJob
    {
        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return JobResult.Success;
        }
    }

    [Fact]
    public async Task EnqueueAsync_TypedJobWithoutArguments_RejectsBeforePersistingAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var client = new JobClient(store);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EnqueueAsync<ArgsConsumingJob>(cancellationToken: token));
        Assert.Empty(await store.QueryAsync(new JobQuery(), token));
    }

    [Fact]
    public async Task RunAsync_WithExecutionContext_ReportsProgressAndIdentityAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "ctx-node", JobTypes = CreateJobRegistry() });

        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "ctx-job",
            Name = "ctx",
            JobType = typeof(ProgressJob).FullName,
            Status = JobStatus.Queued
        }, cancellationToken);

        Assert.True(await worker.RunAsync("ctx-job", cancellationToken));

        var state = await store.GetAsync("ctx-job", cancellationToken);
        Assert.Equal(JobStatus.Completed, state!.Status);
        Assert.Equal(100, state.Progress); // a completed job is 100%; the worker sets this on success
        // The job wrote its context identity + attempt into the progress message (preserved through completion),
        // proving the store-backed context is wired through to job code.
        Assert.Equal("ctx-job:1", state.ProgressMessage);
    }

    [Fact]
    public async Task RunQueuedAsync_RecoversExpiredAdHocAndScheduledJobsAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        await using var provider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        using var worker = new JobWorker(store, provider, new JobWorkerOptions { JobTypes = CreateJobRegistry() });
        var expired = DateTimeOffset.UtcNow.AddMinutes(-5);
        foreach (var id in new[] { "ad-hoc", "scheduled", "exhausted", "healthy" })
        {
            await store.CreateIfAbsentAsync(new JobState
            {
                JobId = id,
                Name = "recovery",
                JobType = typeof(SuccessfulTrackedJob).FullName,
                Status = JobStatus.Processing,
                NodeId = "previous-worker",
                ClaimToken = "previous-claim",
                LeaseExpiresUtc = id == "healthy" ? DateTimeOffset.UtcNow.AddMinutes(5) : expired,
                Attempt = id == "exhausted" ? 3 : 1,
                ScheduledForUtc = id == "scheduled" ? expired : null
            }, token);
        }

        Assert.Equal(2, await worker.RunQueuedAsync(cancellationToken: token));
        Assert.Equal(2, probe.RunCount);
        Assert.Equal(JobStatus.Completed, (await store.GetAsync("ad-hoc", token))!.Status);
        Assert.Equal(JobStatus.Completed, (await store.GetAsync("scheduled", token))!.Status);
        Assert.Equal(JobStatus.Failed, (await store.GetAsync("exhausted", token))!.Status);
        Assert.Equal(JobStatus.Processing, (await store.GetAsync("healthy", token))!.Status);
    }

    [Fact]
    public async Task CreateIfAbsentAsync_WithExistingJob_DoesNotOverwriteStateAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();

        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "job-1",
            Name = "first",
            Status = JobStatus.Queued
        }, cancellationToken);

        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "job-1",
            Name = "second",
            Status = JobStatus.Failed,
            Error = "should not overwrite"
        }, cancellationToken);

        var state = await store.GetAsync("job-1", cancellationToken);

        Assert.NotNull(state);
        Assert.Equal("first", state.Name);
        Assert.Equal(JobStatus.Queued, state.Status);
        Assert.Null(state.Error);
    }

    [Fact]
    public async Task ClaimDueDispatchesAsync_ClaimsReleasesAndCompletesDueDispatchesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var now = DateTimeOffset.UtcNow;

        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "dispatch-1",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = DestinationAddress.ForQueue("work"),
            Body = "hello"u8.ToArray(),
            DueUtc = now.AddSeconds(-1)
        }, cancellationToken);

        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "dispatch-2",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = DestinationAddress.ForQueue("work"),
            Body = "later"u8.ToArray(),
            DueUtc = now.AddHours(1)
        }, cancellationToken);

        var claimed = await store.ClaimDueDispatchesAsync(now, 10, "node-a", TimeSpan.FromMinutes(1), cancellationToken);

        var dispatch = Assert.Single(claimed);
        Assert.Equal("dispatch-1", dispatch.DispatchId);
        Assert.Equal("node-a", dispatch.ClaimOwner);
        Assert.Equal(1, dispatch.Attempts);

        var claimedAgain = await store.ClaimDueDispatchesAsync(now, 10, "node-b", TimeSpan.FromMinutes(1), cancellationToken);
        Assert.Empty(claimedAgain);

        await store.ReleaseDispatchAsync("dispatch-1", "node-a", now.AddSeconds(-1), cancellationToken);

        var reclaimed = await store.ClaimDueDispatchesAsync(now, 10, "node-b", TimeSpan.FromMinutes(1), cancellationToken);
        dispatch = Assert.Single(reclaimed);
        Assert.Equal("node-b", dispatch.ClaimOwner);
        Assert.Equal(2, dispatch.Attempts);

        await store.CompleteDispatchAsync("dispatch-1", "node-b", cancellationToken);

        var afterComplete = await store.ClaimDueDispatchesAsync(now, 10, "node-c", TimeSpan.FromMinutes(1), cancellationToken);
        Assert.Empty(afterComplete);
    }

    [Fact]
    public async Task RunAsync_WhenJobSucceeds_TracksCompletedStateAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry() });

        JobHandle handle = await client.EnqueueAsync<SuccessfulTrackedJob>(new JobRequestOptions { JobId = "job-1" }, cancellationToken);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        var state = await handle.GetStateAsync(cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(1, probe.RunCount);
        Assert.Equal(JobStatus.Completed, state.Status);
        Assert.Equal(1, state.Attempt);
        Assert.Equal(100, state.Progress);
        Assert.NotNull(state.StartedUtc);
        Assert.NotNull(state.CompletedUtc);
        Assert.Null(state.NodeId);
        Assert.Null(state.LeaseExpiresUtc);
    }

    [Fact]
    public async Task EnqueueAsync_WithRegisteredJobType_PersistsStableNameAndWorkerResolvesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        var registry = new JobTypeRegistry([new JobTypeRegistration("search.rebuild", typeof(SuccessfulTrackedJob))]);
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store, jobTypes: registry);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = registry });

        JobHandle handle = await client.EnqueueAsync<SuccessfulTrackedJob>(new JobRequestOptions { JobId = "job-registered" }, cancellationToken);
        var queued = await handle.GetStateAsync(cancellationToken);

        Assert.NotNull(queued);
        Assert.Equal("search.rebuild", queued.JobType);
        Assert.DoesNotContain(",", queued.JobType);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        var completed = await handle.GetStateAsync(cancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(1, probe.RunCount);
    }

    [Fact]
    public async Task RequestCancellationAsync_WhenJobIsRunning_CancelsAndTracksStateAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry() });

        JobHandle handle = await client.EnqueueAsync<CancellableTrackedJob>(new JobRequestOptions { JobId = "job-1" }, cancellationToken);
        var runTask = worker.RunAsync(handle.JobId, cancellationToken);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.True(await handle.RequestCancellationAsync(cancellationToken));

        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(await runTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        var state = await handle.GetStateAsync(cancellationToken);

        Assert.NotNull(state);
        Assert.Equal(JobStatus.Cancelled, state.Status);
        Assert.True(state.CancellationRequested);
        Assert.NotNull(state.CompletedUtc);
        Assert.Null(state.NodeId);
        Assert.Null(state.LeaseExpiresUtc);
    }

    private sealed class JobRuntimeProbe
    {
        private int _runCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount => Volatile.Read(ref _runCount);
        public string? LastMessage { get; private set; }

        public void RecordRun(string? message = null)
        {
            Interlocked.Increment(ref _runCount);
            LastMessage = message;
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithTypedArguments_JobReceivesDeserializedPayloadAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var probe = new JobRuntimeProbe();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry() });

        var handle = await client.EnqueueAsync<ArgsConsumingJob, ResizeArgs>(new ResizeArgs { Path = "/img/1.png", Width = 640 }, cancellationToken: cancellationToken);

        // The payload and its discriminator are durable state, not in-process context.
        var state = await store.GetAsync(handle.JobId, cancellationToken);
        Assert.NotNull(state?.Payload);
        Assert.Equal(typeof(ResizeArgs).FullName, state.PayloadType);

        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));
        Assert.Equal("/img/1.png:640", probe.LastMessage);
        Assert.Equal(JobStatus.Completed, (await handle.GetStateAsync(cancellationToken))!.Status);
    }

    [Fact]
    public async Task RunJob_ResolvesScopedServicesPerExecutionAndDisposesThemAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var tracker = new ScopedLifetimeTracker();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(tracker)
            .AddScoped<ScopedDependency>()
            .AddTransient<ScopedConsumingJob>()
            .BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", JobTypes = CreateJobRegistry() });

        var first = await client.EnqueueAsync<ScopedConsumingJob>(cancellationToken: cancellationToken);
        var second = await client.EnqueueAsync<ScopedConsumingJob>(cancellationToken: cancellationToken);
        Assert.True(await worker.RunAsync(first.JobId, cancellationToken));
        Assert.True(await worker.RunAsync(second.JobId, cancellationToken));

        // Two runs -> two scoped instances (not one root-container singleton), each disposed when its run ended.
        Assert.Equal(2, tracker.Created);
        Assert.Equal(2, tracker.Disposed);
    }

    [Fact]
    public async Task RunQueuedAsync_WithMaxConcurrency_RespectsCapAndRunsInParallelAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        var gauge = new ConcurrencyGauge();
        await using var serviceProvider = new ServiceCollection()
            .AddSingleton(gauge)
            .BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", MaxConcurrency = 2, JobTypes = CreateJobRegistry() });

        for (int i = 0; i < 6; i++)
            await client.EnqueueAsync<ConcurrencyProbeJob>(cancellationToken: cancellationToken);

        Assert.Equal(6, await worker.RunQueuedAsync(cancellationToken: cancellationToken));
        Assert.True(gauge.MaxObserved <= 2, $"expected at most 2 in-flight jobs, observed {gauge.MaxObserved}");
        Assert.True(gauge.MaxObserved > 1, "expected the pool to actually run jobs in parallel");
    }

    private sealed class ScopedLifetimeTracker
    {
        private int _created;
        private int _disposed;
        public int Created => Volatile.Read(ref _created);
        public int Disposed => Volatile.Read(ref _disposed);
        public void RecordCreated() => Interlocked.Increment(ref _created);
        public void RecordDisposed() => Interlocked.Increment(ref _disposed);
    }

    private sealed class ScopedDependency : IDisposable
    {
        private readonly ScopedLifetimeTracker _tracker;

        public ScopedDependency(ScopedLifetimeTracker tracker)
        {
            _tracker = tracker;
            _tracker.RecordCreated();
        }

        public void Dispose() => _tracker.RecordDisposed();
    }

    private sealed class ScopedConsumingJob : IJob
    {
        // The dependency's usefulness is its lifetime tracking; resolving it is the test.
        public ScopedConsumingJob(ScopedDependency dependency) => _ = dependency;

        public Task<JobResult> RunAsync(JobExecutionContext context) => Task.FromResult(JobResult.Success);
    }

    private sealed class ConcurrencyGauge
    {
        private int _inFlight;
        private int _maxObserved;

        public int MaxObserved => Volatile.Read(ref _maxObserved);

        public async Task TrackAsync()
        {
            int current = Interlocked.Increment(ref _inFlight);
            int max;
            while (current > (max = Volatile.Read(ref _maxObserved)))
                Interlocked.CompareExchange(ref _maxObserved, current, max);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private sealed class ConcurrencyProbeJob : IJob
    {
        private readonly ConcurrencyGauge _gauge;

        public ConcurrencyProbeJob(ConcurrencyGauge gauge) => _gauge = gauge;

        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            await _gauge.TrackAsync();
            return JobResult.Success;
        }
    }

    private sealed class ResizeArgs
    {
        public string? Path { get; set; }
        public int Width { get; set; }
    }

    private sealed class ArgsConsumingJob : IJob<ResizeArgs>
    {
        private readonly JobRuntimeProbe _probe;

        public ArgsConsumingJob(JobRuntimeProbe probe)
        {
            _probe = probe;
        }

        public Task<JobResult> RunAsync(ResizeArgs args, JobExecutionContext context)
        {
            _probe.RecordRun($"{args.Path}:{args.Width}");
            return Task.FromResult(JobResult.Success);
        }
    }

    private sealed class SuccessfulTrackedJob : IJob
    {
        private readonly JobRuntimeProbe _probe;

        public SuccessfulTrackedJob(JobRuntimeProbe probe)
        {
            _probe = probe;
        }

        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            _probe.RecordRun();
            return Task.FromResult(JobResult.Success);
        }
    }

    private sealed class CancellableTrackedJob : IJob
    {
        private readonly JobRuntimeProbe _probe;

        public CancellableTrackedJob(JobRuntimeProbe probe)
        {
            _probe = probe;
        }

        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            _probe.Started.TrySetResult();

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
                return JobResult.Success;
            }
            catch (OperationCanceledException)
            {
                _probe.Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ProgressJob : IJob
    {
        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            await context.ReportProgressAsync(75, $"{context.JobId}:{context.Attempt}", context.CancellationToken);
            return JobResult.Success;
        }
    }
}
