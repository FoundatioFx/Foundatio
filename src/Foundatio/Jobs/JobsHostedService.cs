using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>
/// Runs the worker when job types are registered and the scheduler when CRON jobs are registered. When the host
/// begins stopping, both stop taking work at once, together with the message handlers, and running jobs then drain
/// while the host stops its other services.
/// </summary>
internal sealed class JobsHostedService : IHostedLifecycleService, IAsyncDisposable
{
    private readonly JobsRuntime _runtime;
    private readonly JobClient _client;
    private readonly IStartupGate? _startupGate;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _stopLock = new();
    private Task _gatedStart = Task.CompletedTask;
    private Task? _stop;
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

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops claiming and scheduling as soon as the host begins stopping, before any hosted service's <see cref="StopAsync"/>, and starts draining.</summary>
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        BeginStop(cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Waits for the drain <see cref="StoppingAsync"/> started, or stops now when the host did not call it.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => BeginStop(cancellationToken);

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops the worker and scheduler when the host is disposed without being stopped, for example a test's
    /// <c>using var host</c>. Running jobs are cancelled at once; they run again without using an attempt.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync().AnyContext();
            await StopAsync(cancelled.Token).AnyContext();
        }

        _stopping.Dispose();
    }

    private Task BeginStop(CancellationToken cancellationToken)
    {
        lock (_stopLock)
            return _stop ??= StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        await _gatedStart.AnyContext();
        if (_runtime.HostState != ProcessingState.Faulted)
            _runtime.HostState = ProcessingState.Stopped;

        // Both stop taking work before either waits: the scheduler for its pass, the worker for its running jobs.
        await Task.WhenAll(_scheduler?.StopAsync() ?? Task.CompletedTask, _worker?.StopAsync(cancellationToken) ?? Task.CompletedTask).AnyContext();
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
