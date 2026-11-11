using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>Runs the worker when job types are registered and the scheduler when CRON jobs are registered.</summary>
internal sealed class JobsHostedService : IHostedService
{
    private readonly JobsRuntime _runtime;
    private readonly JobClient _client;
    private readonly IStartupGate? _startupGate;
    private readonly CancellationTokenSource _stopping = new();
    private Task _gatedStart = Task.CompletedTask;
    private JobWorker? _worker;
    private JobScheduler? _scheduler;

    public JobsHostedService(JobsRuntime runtime, JobClient client, IServiceProvider services)
    {
        _runtime = runtime;
        _client = client;
        _startupGate = (IStartupGate?)services.GetService(typeof(IStartupGate));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runtime.Jobs.Count == 0 && _runtime.Registrations.Schedules.Count == 0)
            return Task.CompletedTask;

        if (_startupGate is null)
        {
            Start();
            return Task.CompletedTask;
        }

        _gatedStart = Task.Run(async () =>
        {
            try
            {
                if (await _startupGate.WaitAsync(_stopping.Token).AnyContext())
                {
                    Start();
                }
                else
                {
                    _runtime.HostState = ProcessingState.Faulted;
                    _runtime.LoggerFactory.CreateLogger<JobsHostedService>().LogCritical("Startup failed; jobs will not run");
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        await _gatedStart.AnyContext();
        if (_runtime.HostState != ProcessingState.Faulted)
            _runtime.HostState = ProcessingState.Stopped;

        if (_scheduler is not null)
            await _scheduler.StopAsync().AnyContext();

        if (_worker is not null)
            await _worker.StopAsync(cancellationToken).AnyContext();
    }

    private void Start()
    {
        _runtime.HostState = ProcessingState.Running;
        if (_runtime.Jobs.Count > 0)
        {
            _worker = new JobWorker(_runtime);
            _runtime.Worker = _worker;
            _worker.Start();
        }

        if (_runtime.Registrations.Schedules.Count > 0 && _runtime.RunSchedules)
        {
            _scheduler = new JobScheduler(_runtime, _client);
            _runtime.Scheduler = _scheduler;
            _scheduler.Start();
        }
    }
}
