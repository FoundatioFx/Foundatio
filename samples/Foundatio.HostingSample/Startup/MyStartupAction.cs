using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

public class MyStartupAction : IStartupAction
{
    private readonly ScheduledJobManager _scheduledJobManager;
    private readonly ILogger _logger;

    public MyStartupAction(ScheduledJobManager scheduledJobManager, ILogger<MyStartupAction> logger)
    {
        _scheduledJobManager = scheduledJobManager;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < 5; i++)
        {
            _logger.LogTrace("MyStartupAction Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
            await Task.Delay(500);
        }

        _scheduledJobManager.AddOrUpdate("MyJob", "* * * * *", async () =>
        {
            _logger.LogInformation("Running MyJob");
            await Task.Delay(1000);
            _logger.LogInformation("MyJob Complete");
        });
    }
}
