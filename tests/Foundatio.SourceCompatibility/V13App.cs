using Foundatio;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Storage;
using Foundatio.Utility;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly;
using StackExchange.Redis;

namespace V13App;

public record OrderPlaced(int OrderId);

public class WorkItem
{
    public int Id { get; set; }
}

public class OrderOptions
{
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}

[Job(Name = "Cleanup", Interval = "1m")]
public class CleanupJob : JobBase
{
    private readonly ICacheClient _cache;

    public CleanupJob(ICacheClient cache, ILoggerFactory loggerFactory) : base(loggerFactory)
    {
        _cache = cache;
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        await _cache.RemoveByPrefixAsync("orders:");
        return JobResult.Success;
    }
}

public class ReportJob : IJob
{
    public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) => Task.FromResult(JobResult.SuccessWithMessage("done"));
}

public class WorkItemQueueJob : QueueJobBase<WorkItem>
{
    public WorkItemQueueJob(IQueue<WorkItem> queue, TimeProvider timeProvider, IResiliencePolicyProvider resiliencePolicyProvider, ILoggerFactory loggerFactory)
        : base(queue, timeProvider, resiliencePolicyProvider, loggerFactory)
    {
    }

    protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<WorkItem> context)
        => Task.FromResult(context.QueueEntry.Value.Id > 0 ? JobResult.Success : JobResult.FailedWithMessage("invalid id"));
}

public class OrderService
{
    private readonly IMessageBus _messageBus;
    private readonly IQueue<WorkItem> _queue;
    private readonly ICacheClient _cache;
    private readonly ILockProvider _locks;
    private readonly IFileStorage _storage;
    private readonly ISerializer _serializer;
    private readonly IResiliencePolicyProvider _resilience;
    private readonly IConnectionMultiplexer _redis;
    private readonly IOptions<OrderOptions> _options;
    private readonly ILogger<OrderService> _logger;
    private readonly TimeProvider _timeProvider;

    public OrderService(IMessageBus messageBus, IQueue<WorkItem> queue, ICacheClient cache, ILockProvider locks, IFileStorage storage, ISerializer serializer,
        IResiliencePolicyProvider resilience, IConnectionMultiplexer redis, IOptions<OrderOptions> options, ILogger<OrderService> logger, TimeProvider timeProvider)
    {
        _messageBus = messageBus;
        _queue = queue;
        _cache = cache;
        _locks = locks;
        _storage = storage;
        _serializer = serializer;
        _resilience = resilience;
        _redis = redis;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task PlaceAsync(int orderId, CancellationToken cancellationToken)
    {
        await using var orderLock = await _locks.AcquireAsync($"order:{orderId}", cancellationToken: cancellationToken);
        if (orderLock is null)
            return;

        IResiliencePolicy policy = _resilience.GetDefaultPolicy();
        await policy.ExecuteAsync(async ct =>
        {
            await _queue.EnqueueAsync(new WorkItem { Id = orderId });
            await _messageBus.PublishAsync(new OrderPlaced(orderId), cancellationToken: ct);
        }, cancellationToken);

        await _cache.SetAsync($"orders:{orderId}", _timeProvider.GetUtcNow(), _options.Value.CacheDuration);
        await _storage.SaveFileAsync($"orders/{orderId}.json", new MemoryStream(_serializer.SerializeToBytes(new OrderPlaced(orderId))), cancellationToken);
        await _redis.GetDatabase().StringIncrementAsync("orders:count");
        _logger.LogInformation("Placed order {OrderId}", orderId);
    }
}

public class OrderSubscriber : BackgroundService
{
    private readonly IMessageSubscriber _subscriber;
    private readonly ILogger<OrderSubscriber> _logger;

    public OrderSubscriber(IMessageSubscriber subscriber, ILogger<OrderSubscriber> logger)
    {
        _subscriber = subscriber;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _subscriber.SubscribeAsync<OrderPlaced>(order => _logger.LogInformation("Order {OrderId} placed", order.OrderId), stoppingToken);
}

public class QueueHealthCheck : IHealthCheck
{
    private readonly IQueue<WorkItem> _queue;

    public QueueHealthCheck(IQueue<WorkItem> queue)
    {
        _queue = queue;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        QueueStats stats = await _queue.GetQueueStatsAsync();
        return stats.Deadletter > 100 ? HealthCheckResult.Degraded($"{stats.Deadletter} dead letters") : HealthCheckResult.Healthy();
    }
}

public class AdminService
{
    private readonly IJobManager _jobManager;
    private readonly IServiceProvider _services;

    public AdminService(IJobManager jobManager, IServiceProvider services)
    {
        _jobManager = jobManager;
        _services = services;
    }

    public JobStatus[] GetAll() => _jobManager.GetJobStatus();

    public JobStatus GetOne(string name) => _jobManager.GetJobStatus(name);

    public Task<bool> RunCleanupAsync(CancellationToken cancellationToken)
        => new JobRunner(JobOptions.GetDefaults<CleanupJob>(), _services).RunAsync(cancellationToken);

    public async Task<int> CountWithLockAsync(CancellationToken cancellationToken)
    {
        var asyncLock = new AsyncLock();
        using (await asyncLock.LockAsync(cancellationToken))
            return _jobManager.GetJobStatus(runningOnly: true).Length;
    }

    public ResiliencePipeline CreatePipeline() => ResiliencePipeline.Empty;

    public ResiliencePolicy CreateFoundatioPolicy() => new ResiliencePolicyBuilder().WithMaxAttempts(3).Build();
}
