using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly JobManager _jobManager;

    public ScheduledJobService(IServiceProvider serviceProvider, JobManager jobManager)
    {
        _serviceProvider = serviceProvider;
        _jobManager = jobManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupContext = _serviceProvider.GetService<StartupActionsContext>();
        if (startupContext != null)
        {
            var result = await startupContext.WaitForStartupAsync(stoppingToken).AnyContext();
            if (!result.Success)
            {
                throw new ApplicationException("Failed to wait for startup actions to complete");
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var jobToRun in _jobManager.Jobs)
            {
                if (await jobToRun.ShouldRunAsync())
                    await jobToRun.StartAsync(stoppingToken).AnyContext();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).AnyContext();
        }
    }
}
