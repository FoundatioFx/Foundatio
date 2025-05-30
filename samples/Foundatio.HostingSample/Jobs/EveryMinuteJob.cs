using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

public class EveryMinuteJob : IJob
{
    private readonly ICacheClient _cacheClient;
    private readonly ILogger _logger;

    public EveryMinuteJob(ILoggerFactory loggerFactory, ICacheClient cacheClient)
    {
        _cacheClient = cacheClient;
        _logger = loggerFactory.CreateLogger<EveryMinuteJob>();
    }

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var runCount = await _cacheClient.IncrementAsync("EveryMinuteJob");

        _logger.LogInformation("EveryMinuteJob Run Count={Count} Thread={ManagedThreadId}", runCount, Thread.CurrentThread.ManagedThreadId);

        await Task.Delay(TimeSpan.FromSeconds(30));

        _logger.LogInformation("EveryMinuteJob Complete");

        return JobResult.Success;
    }
}
