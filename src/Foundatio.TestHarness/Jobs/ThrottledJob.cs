using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Jobs;

public class ThrottledJob : JobWithLockBase
{
    public ThrottledJob(ICacheClient client, ILoggerFactory loggerFactory = null) : base(loggerFactory)
    {
        _locker = new ThrottlingLockProvider(client, 1, TimeSpan.FromMilliseconds(100), null, loggerFactory);
    }

    private readonly ILockProvider _locker;
    public int RunCount { get; set; }

    protected override Task<ILock> GetLockAsync(CancellationToken cancellationToken = default)
    {
        return _locker.AcquireAsync(nameof(ThrottledJob), acquireTimeout: TimeSpan.Zero);
    }

    protected override Task<JobResult> RunInternalAsync(JobContext context)
    {
        RunCount++;
        _logger.LogDebug("Incremented Run Count: {RunCount}", RunCount);
        return Task.FromResult(JobResult.Success);
    }
}
