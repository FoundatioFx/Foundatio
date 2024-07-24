using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Jobs;

public class HostedJobService : IHostedService, IJobStatus, IDisposable
{
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task _executingTask;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly HostedJobOptions _jobOptions;
    private bool _hasStarted = false;

    public HostedJobService(IServiceProvider serviceProvider, HostedJobOptions jobOptions, ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HostedJobService>();
        _jobOptions = jobOptions;

        var lifetime = serviceProvider.GetService<ShutdownHostIfNoJobsRunningService>();
        lifetime?.RegisterHostedJobInstance(this);
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_jobOptions.WaitForStartupActions)
        {
            var startupContext = _serviceProvider.GetService<StartupActionsContext>();
            if (startupContext != null)
            {
                var result = await startupContext.WaitForStartupAsync(stoppingToken).AnyContext();
                if (!result.Success)
                {
                    _logger.LogError("Unable to start {JobName} job due to startup actions failure", _jobOptions.Name);
                    return;
                }
            }
        }

        var runner = new JobRunner(_jobOptions, _serviceProvider, _loggerFactory);

        try
        {
            using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Job " + _jobOptions.Name, ActivityKind.Server);

            await runner.RunAsync(stoppingToken).AnyContext();
#if NET8_0_OR_GREATER
            await _stoppingCts.CancelAsync().AnyContext();
#else
            _stoppingCts.Cancel();
#endif
        }
        finally
        {
            _logger.LogInformation("{JobName} job completed", _jobOptions.Name);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _executingTask = ExecuteAsync(_stoppingCts.Token);
        _hasStarted = true;
        return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executingTask == null)
            return;

        try
        {
#if NET8_0_OR_GREATER
            await _stoppingCts.CancelAsync().AnyContext();
#else
            _stoppingCts.Cancel();
#endif
        }
        finally
        {
            await Task.WhenAny(_executingTask, Task.Delay(-1, cancellationToken)).AnyContext();
        }
    }

    public void Dispose()
    {
        _stoppingCts.Cancel();
        _stoppingCts.Dispose();
    }

    public bool IsRunning => _hasStarted == false || (_executingTask != null && !_executingTask.IsCompleted);
}

public interface IJobStatus
{
    bool IsRunning { get; }
}
