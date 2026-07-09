using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

/// <summary>Cadence and batch size for the durable job-runtime pump.</summary>
public class JobRuntimePumpOptions
{
    /// <summary>
    /// Whether the auto-registered runtime pump runs. Default true. Set false to take manual control of pumping (e.g.
    /// drive <see cref="JobScheduleProcessor"/>/<see cref="IJobWorker"/> yourself, or run the pump on only some nodes);
    /// the hosted service is then registered but does nothing. Configure via <c>AddFoundatio().Jobs.ConfigureRuntimePump</c>
    /// or <c>AddJobRuntimeService</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the pump materializes CRON occurrences, dispatches due work, and runs queued jobs. Default 1s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of due dispatches and queued jobs claimed per iteration. Default 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Maximum processing attempts for an ad-hoc job before a stale (lease-expired) instance is dead-lettered. Default 3.</summary>
    public int MaxJobAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum queued jobs the worker executes concurrently per node. Default 1, which preserves per-node run
    /// ordering; raise it for I/O-bound jobs. Every in-flight job gets its own DI scope, lease, and cancellation watcher.
    /// </summary>
    public int WorkerConcurrency { get; set; } = 1;
}

/// <summary>
/// Drives the durable job runtime (<see cref="IJobRuntimeStore"/>): materializes CRON occurrences, dispatches
/// delayed/scheduled work (including the messaging delayed-delivery fallback), recovers stale occurrences, and runs
/// jobs submitted via <see cref="IJobClient"/>. Registered automatically whenever a runtime store is configured
/// (<c>AddFoundatio().Jobs.UseInMemoryRuntime()</c> / <c>UseRuntimeStore()</c>) so a configured store can never
/// silently accumulate work that nothing drains. In a non-hosted process (no generic host) it is simply never started.
/// </summary>
public class JobRuntimePumpService : BackgroundService
{
    private readonly JobScheduleProcessor _processor;
    private readonly IJobWorker _worker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly JobRuntimePumpOptions _options;
    private readonly IJobScheduler? _scheduler;
    private readonly IEnumerable<ScheduledJobDefinition> _scheduledJobs;

    public JobRuntimePumpService(JobScheduleProcessor processor, IJobWorker worker, TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null, JobRuntimePumpOptions? options = null, IJobScheduler? scheduler = null, IEnumerable<ScheduledJobDefinition>? scheduledJobs = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<JobRuntimePumpService>();
        _options = options ?? new JobRuntimePumpOptions();
        _scheduler = scheduler;
        _scheduledJobs = scheduledJobs ?? Array.Empty<ScheduledJobDefinition>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Schedule CRON jobs registered declaratively via AddFoundatio().Jobs.AddCronJob<T>() so users don't have to
        // call IJobScheduler.ScheduleAsync themselves. Done before the Enabled check so the "scheduled automatically"
        // contract holds even when this node's pump is disabled for manual control. Idempotent (schedule keyed by name),
        // so every node registering the same schedules is fine.
        if (_scheduler is not null)
        {
            foreach (var definition in _scheduledJobs)
            {
                try
                {
                    await _scheduler.ScheduleAsync(definition, stoppingToken).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to schedule CRON job {JobName}: {Message}", definition.Name, ex.Message);
                }
            }
        }

        if (!_options.Enabled)
        {
            _logger.LogInformation("Job runtime pump disabled (JobRuntimePumpOptions.Enabled = false); not pumping the runtime store");
            return;
        }

        _logger.LogInformation("Job runtime pump starting (poll interval {PollInterval}, batch size {BatchSize}, worker concurrency {WorkerConcurrency})", _options.PollInterval, _options.BatchSize, _options.WorkerConcurrency);

        // Execution (dispatching due work and running jobs) is an overlapped pass: the scheduling stage below keeps
        // its cadence every poll even while a long job runs, so CRON materialization and the messaging delayed-
        // delivery fallback are never head-of-line blocked by job duration. At most one pass is in flight; if the
        // prior pass is still running when the loop comes around, this tick only materializes. Overlap-adjacent races
        // are safe: dispatch claims are leased and job claims are TryTransition-guarded, so nothing double-runs.
        var executionPass = Task.CompletedTask;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = _timeProvider.GetUtcNow();

                // Materialize CRON occurrences due within the misfire window (deduped, idempotent).
                await _processor.EnqueueDueOccurrencesAsync(now, stoppingToken).AnyContext();

                if (executionPass.IsCompleted)
                    executionPass = Task.Run(() => RunExecutionPassAsync(now, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pumping job runtime: {Message}", ex.Message);
            }

            try
            {
                await _timeProvider.Delay(_options.PollInterval, stoppingToken).AnyContext();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Drain the in-flight pass so shutdown does not abandon running jobs mid-settlement.
        try
        {
            await executionPass.AnyContext();
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("Job runtime pump stopped");
    }

    private async Task RunExecutionPassAsync(DateTimeOffset now, CancellationToken stoppingToken)
    {
        try
        {
            // Claim and run due dispatches: delayed queue/pub-sub messages first, then CRON occurrences, recovering
            // occurrences whose processing lease expired and applying retry/dead-letter.
            await _processor.RunDueOccurrencesAsync(now, _options.BatchSize, lease: null, stoppingToken).AnyContext();

            // Recover ad-hoc (non-CRON) jobs whose processing lease expired (a worker crash mid-run).
            await _worker.RecoverStaleAsync(_options.MaxJobAttempts, _options.BatchSize, stoppingToken).AnyContext();

            // Run jobs submitted via IJobClient sitting in the Queued state.
            await _worker.RunQueuedAsync(_options.BatchSize, stoppingToken).AnyContext();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running job runtime execution pass: {Message}", ex.Message);
        }
    }
}
