using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Extensions.Hosting.Jobs;

/// <summary>
/// Options controlling the cadence and batch size of <see cref="JobRuntimeService"/>.
/// </summary>
public class JobRuntimeServiceOptions
{
    /// <summary>Whether the runtime pump runs. Default true; set false to take manual control of pumping.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the runtime pump materializes CRON occurrences, dispatches due work, and runs queued jobs.
    /// Defaults to one second so sub-minute CRON schedules and short delays are honored.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum number of due dispatches and queued jobs claimed per pump iteration.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of processing attempts for a durable job before a stale (lease-expired) instance is
    /// dead-lettered instead of re-queued. Defaults to 3.
    /// </summary>
    public int MaxJobAttempts { get; set; } = 3;
}

/// <summary>
/// Drives the durable job runtime introduced by <see cref="IJobRuntimeStore"/>. Without this hosted service nothing
/// materializes CRON occurrences, dispatches delayed/scheduled work, recovers stale (lease-expired) occurrences, or
/// runs jobs submitted through <see cref="IJobClient"/> — the runtime store would accumulate work that never executes.
/// </summary>
public class JobRuntimeService : BackgroundService
{
    private readonly JobScheduleProcessor _processor;
    private readonly IJobWorker _worker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly JobRuntimeServiceOptions _options;

    public JobRuntimeService(JobScheduleProcessor processor, IJobWorker worker, TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null, JobRuntimeServiceOptions? options = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<JobRuntimeService>();
        _options = options ?? new JobRuntimeServiceOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Job runtime pump disabled (Enabled = false); not pumping the runtime store");
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

                // Claim and run due dispatches: CRON occurrences plus delayed queue/pub-sub messages. This also
                // recovers occurrences whose processing lease expired (crash mid-run) and applies retry/dead-letter.
                await _processor.RunDueOccurrencesAsync(now, _options.BatchSize, lease: null, stoppingToken).AnyContext();

                // Recover plain (non-CRON) jobs whose processing lease expired (a worker crash mid-run): re-queue them
                // while attempts remain, otherwise dead-letter them. Without this they would strand in Processing.
                await _worker.RecoverStaleAsync(_options.MaxJobAttempts, _options.BatchSize, stoppingToken).AnyContext();

                // Run jobs submitted via IJobClient that are sitting in the Queued state.
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
