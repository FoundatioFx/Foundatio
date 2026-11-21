using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Benchmarks.Messaging;

/// <summary>Runs <see cref="BenchPayload"/> jobs by signalling the shared countdown.</summary>
public sealed class BenchJobHandler : IJobHandler<BenchPayload>
{
    private readonly Countdown _countdown;

    public BenchJobHandler(Countdown countdown)
    {
        _countdown = countdown;
    }

    public Task HandleAsync(BenchPayload arguments, IJobContext context, CancellationToken cancellationToken)
    {
        _countdown.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory job store that can be swapped for an empty one, so enqueue benchmarks do not grow it.</summary>
public sealed class SwappableJobStore : IJobStore
{
    private InMemoryJobStore _inner = new();

    public void Reset() => _inner = new InMemoryJobStore();

    public Task<bool> CreateAsync(JobInfo job, CancellationToken cancellationToken = default) => _inner.CreateAsync(job, cancellationToken);
    public Task<JobInfo> GetAsync(string jobId, CancellationToken cancellationToken = default) => _inner.GetAsync(jobId, cancellationToken);
    public Task<JobPage<JobInfo>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default) => _inner.QueryAsync(query, cancellationToken);
    public Task<long> CountAsync(JobState? state = null, string name = null, CancellationToken cancellationToken = default) => _inner.CountAsync(state, name, cancellationToken);
    public Task<JobInfo> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default) => _inner.ClaimAsync(request, cancellationToken);
    public Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => _inner.RenewAsync(jobId, claimToken, leaseDuration, cancellationToken);
    public Task<bool> ReportProgressAsync(string jobId, string claimToken, int? percent, string message, CancellationToken cancellationToken = default) => _inner.ReportProgressAsync(jobId, claimToken, percent, message, cancellationToken);
    public Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default) => _inner.CompleteAsync(jobId, claimToken, completion, cancellationToken);
    public Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default) => _inner.RequestCancellationAsync(jobId, retainFor, cancellationToken);
    public Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(jobId, cancellationToken);
}

/// <summary>
/// Durable jobs on the in-memory job store, 100-byte arguments: enqueueing without a worker, and enqueue to run with a
/// worker at concurrency 8 (each job enqueued and awaited in turn, so the worker claims as they arrive), against the
/// legacy <see cref="InMemoryQueue{T}"/> worked by 8 <see cref="QueueJobBase{T}"/> loops. Each operation is one job.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[BenchmarkCategory("Jobs")]
public class DurableJobBenchmarks
{
    private const int Batch = 1000;
    private const int Concurrency = 8;

    private readonly Countdown _countdown = new();
    private readonly BenchPayload _payload = BenchPayload.Create(100);
    private SwappableJobStore _store;
    private BenchHost _host;
    private IJobClient _client;
    private InMemoryQueue<BenchPayload> _legacyQueue;
    private LegacyQueueWorkers _legacyWorkers;

    [GlobalSetup(Target = nameof(V2Enqueue))]
    public async Task SetupEnqueueAsync()
    {
        _store = new SwappableJobStore();
        _host = await BenchHost.StartAsync(s => s.AddFoundatioJobs(j => j.UseStore(_store)));
        _client = _host.Services.GetRequiredService<IJobClient>();
    }

    [GlobalSetup(Targets = [nameof(V2EnqueueAndRun), nameof(V2EnqueueAndRunLatency)])]
    public async Task SetupRunAsync()
    {
        _host = await BenchHost.StartAsync(s => s
            .AddSingleton(_countdown)
            .AddFoundatioJobs(j => j
                .UseInMemory()
                .Configure(o =>
                {
                    o.MaxConcurrency = Concurrency;
                    o.RetainSucceededFor = TimeSpan.FromMilliseconds(1);
                })
                .AddJob<BenchJobHandler>()));
        _client = _host.Services.GetRequiredService<IJobClient>();
    }

    [GlobalSetup(Target = nameof(LegacyQueueEnqueue))]
    public void SetupLegacyEnqueue() => _legacyQueue = new InMemoryQueue<BenchPayload>();

    [GlobalSetup(Targets = [nameof(LegacyQueueEnqueueAndRun), nameof(LegacyQueueEnqueueAndRunLatency)])]
    public void SetupLegacyRun()
    {
        _legacyQueue = new InMemoryQueue<BenchPayload>();
        _legacyWorkers = new LegacyQueueWorkers(_legacyQueue, Concurrency, _ =>
        {
            _countdown.Signal();
            return JobResult.Success;
        });
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (_legacyWorkers is not null)
            await _legacyWorkers.DisposeAsync();
        _legacyQueue?.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Enqueue")]
    public async Task V2Enqueue()
    {
        for (int i = 0; i < Batch; i++)
            await _client.EnqueueAsync(_payload);

        _store.Reset();
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Enqueue")]
    public async Task LegacyQueueEnqueue()
    {
        for (int i = 0; i < Batch; i++)
            await _legacyQueue.EnqueueAsync(_payload);

        await _legacyQueue.DeleteQueueAsync();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    [BenchmarkCategory("EnqueueAndRun")]
    public async Task V2EnqueueAndRun()
    {
        var done = _countdown.Reset(Batch);
        for (int i = 0; i < Batch; i++)
            await _client.EnqueueAsync(_payload);

        await done.WaitOrThrowAsync(nameof(V2EnqueueAndRun));
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("EnqueueAndRun")]
    public async Task LegacyQueueEnqueueAndRun()
    {
        var done = _countdown.Reset(Batch);
        for (int i = 0; i < Batch; i++)
            await _legacyQueue.EnqueueAsync(_payload);

        await done.WaitOrThrowAsync(nameof(LegacyQueueEnqueueAndRun));
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Latency")]
    public async Task V2EnqueueAndRunLatency()
    {
        var done = _countdown.Reset(1);
        await _client.EnqueueAsync(_payload);
        await done;
    }

    [Benchmark]
    [BenchmarkCategory("Latency")]
    public async Task LegacyQueueEnqueueAndRunLatency()
    {
        var done = _countdown.Reset(1);
        await _legacyQueue.EnqueueAsync(_payload);
        await done;
    }
}
