using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

public class MyStartupAction : IStartupAction
{
    private readonly IJobManager _jobManager;
    private readonly ICacheClient _cacheClient;
    private readonly ILogger _logger;

    public MyStartupAction(IJobManager jobManager, ICacheClient cacheClient, ILogger<MyStartupAction> logger)
    {
        _jobManager = jobManager;
        _cacheClient = cacheClient;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // set next run to be far in the past so it runs immediately
        await _cacheClient.SetAsync("jobs:every_minute:nextrun", DateTime.UtcNow.AddDays(-1));

        for (int i = 0; i < 5; i++)
        {
            _logger.LogTrace("MyStartupAction Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
            await Task.Delay(500);
        }

        _jobManager.AddOrUpdate("MyJob", j => j.CronSchedule("* * * * *").JobAction(async () =>
        {
            _logger.LogInformation("Running MyJob");
            await Task.Delay(1000);
            _logger.LogInformation("MyJob Complete");
        }));
    }
}
