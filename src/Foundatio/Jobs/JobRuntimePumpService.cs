using System;
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

    public JobRuntimePumpService(JobScheduleProcessor processor, IJobWorker worker, TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null, JobRuntimePumpOptions? options = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<JobRuntimePumpService>();
        _options = options ?? new JobRuntimePumpOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Job runtime pump disabled (JobRuntimePumpOptions.Enabled = false); not pumping the runtime store");
            return;
        }

        _logger.LogInformation("Job runtime pump starting (poll interval {PollInterval}, batch size {BatchSize})", _options.PollInterval, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = _timeProvider.GetUtcNow();

                // Materialize CRON occurrences due within the misfire window (deduped, idempotent).
                await _processor.EnqueueDueOccurrencesAsync(now, stoppingToken).AnyContext();

                // Claim and run due dispatches: CRON occurrences plus delayed queue/pub-sub messages, recovering
                // occurrences whose processing lease expired and applying retry/dead-letter.
                await _processor.RunDueOccurrencesAsync(now, _options.BatchSize, lease: null, stoppingToken).AnyContext();

                // Recover ad-hoc (non-CRON) jobs whose processing lease expired (a worker crash mid-run).
                await _worker.RecoverStaleAsync(_options.MaxJobAttempts, _options.BatchSize, stoppingToken).AnyContext();

                // Run jobs submitted via IJobClient sitting in the Queued state.
                await _worker.RunQueuedAsync(_options.BatchSize, stoppingToken).AnyContext();
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

        _logger.LogInformation("Job runtime pump stopped");
    }
}
