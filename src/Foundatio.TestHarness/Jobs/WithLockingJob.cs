using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class WithLockingJob : JobWithLockBase
{
    private readonly ILockProvider _locker;

    public WithLockingJob(ILoggerFactory loggerFactory) : base(loggerFactory)
    {
        _locker = new CacheLockProvider(new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory)), new InMemoryMessageBus(o => o.LoggerFactory(loggerFactory)), null, loggerFactory);
    }

    public int RunCount { get; set; }

    protected override Task<ILock> GetLockAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        return _locker.AcquireAsync(nameof(WithLockingJob), TimeSpan.FromSeconds(1), TimeSpan.Zero);
    }

    protected override async Task<JobResult> RunInternalAsync(JobContext context)
    {
        RunCount++;

        await Task.Delay(150, context.CancellationToken);
        Assert.True(await _locker.IsLockedAsync("WithLockingJob"));

        return JobResult.Success;
    }
}
