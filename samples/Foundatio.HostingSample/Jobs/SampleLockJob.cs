using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

[Job(Description = "Sample lock job", Interval = "5s")]
public class SampleLockJob : JobWithLockBase
{
    private readonly ILockProvider _lockProvider;

    public SampleLockJob(ICacheClient cache)
    {
        _lockProvider = new ThrottlingLockProvider(cache, 1, TimeSpan.FromMinutes(1));;
    }

    protected override Task<ILock> GetLockAsync(CancellationToken cancellationToken = default)
    {
        return _lockProvider.AcquireAsync(nameof(SampleLockJob), TimeSpan.FromMinutes(15), new CancellationToken(true));
    }

    protected override Task<JobResult> RunInternalAsync(JobContext context)
    {
        _logger.LogTrace("SampleLockJob Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);

        return Task.FromResult(JobResult.Success);
    }
}
