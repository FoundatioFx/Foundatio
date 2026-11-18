using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
public class WorkItemLeaseBenchmarks
{
    private readonly InMemoryQueue<WorkItemData> _queue = new();
    private readonly InMemoryCacheClient _cache = new();
    private readonly InMemoryMessageBus _bus = new();
    private WorkItemJob _job = null!;

    [Params(false, true)]
    public bool ReportProgress { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new Handler(new CacheLockProvider(_cache, _bus), ReportProgress) { AutoRenewLockOnProgress = true });
        _job = new WorkItemJob(_queue, _bus, handlers);
    }

    [Benchmark]
    public async Task ProcessAsync()
    {
        await _queue.EnqueueAsync("benchmark", ReportProgress);
        var result = await _job.RunAsync();
        if (!result.IsSuccess)
            throw new System.InvalidOperationException(result.Message, result.Error);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queue.Dispose();
        _bus.Dispose();
        _cache.Dispose();
    }

    private sealed class Handler(ILockProvider locker, bool reportProgress) : WorkItemHandlerBase
    {
        public override Task<ILock> GetWorkItemLockAsync(object item, CancellationToken cancellationToken = default) =>
            locker.TryAcquireAsync("work-item", cancellationToken: cancellationToken);

        public override Task HandleItemAsync(WorkItemContext context) =>
            reportProgress ? context.ReportProgressAsync(50) : Task.CompletedTask;
    }
}
