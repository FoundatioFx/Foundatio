using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

internal sealed class JobRunContext : IJobContext
{
    private static readonly AsyncLocal<JobRunContext?> CurrentContext = new();

    private readonly IJobStore _store;
    private readonly string _claimToken;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;
    private ProgressReport? _pending;

    public JobRunContext(JobInfo job, IJobStore store, TimeProvider timeProvider, ILogger logger)
    {
        JobId = job.Id;
        JobName = job.Name;
        Attempt = job.Attempt;
        CorrelationId = job.CorrelationId;
        ScheduledFor = job.AvailableAt;
        ScheduleName = job.ScheduleName;
        Headers = job.Headers;
        _store = store;
        _claimToken = job.ClaimToken!;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string JobId { get; }
    public string JobName { get; }
    public int Attempt { get; }
    public string? CorrelationId { get; }
    public DateTimeOffset ScheduledFor { get; }
    public string? ScheduleName { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The latest reported message, whether or not it has been saved yet.</summary>
    public string? Message { get; private set; }

    /// <summary>The latest reported percentage, whether or not it has been saved yet.</summary>
    public int? Progress { get; private set; }

    /// <summary>The job running on this async flow, used to propagate the correlation id.</summary>
    public static JobRunContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    public async Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        HandlerProgress.ThrowIfInvalid(percent);

        lock (_lock)
        {
            _pending = new ProgressReport(percent, message).Over(_pending);
            Message = message ?? Message;
            Progress = percent ?? Progress;
            var now = _timeProvider.GetUtcNow();
            if (now - _lastReport < HandlerProgress.Interval && percent != 100)
                return;

            _lastReport = now;
        }

        try
        {
            await WritePendingAsync(cancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not save the progress of job {JobId}; it will be saved with the next report", JobId);
        }
    }

    /// <summary>Writes a throttled or failed progress report that has not been stored yet.</summary>
    public Task FlushAsync(CancellationToken cancellationToken) => WritePendingAsync(cancellationToken);

    /// <summary>
    /// Writes the latest unsaved report. Writes are serialized so a slow write cannot overwrite a newer one, and a
    /// failed report stays pending under any newer values.
    /// </summary>
    private async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).AnyContext();
        try
        {
            ProgressReport report;
            lock (_lock)
            {
                if (_pending is not { } pending)
                    return;

                report = pending;
                _pending = null;
            }

            try
            {
                await _store.ReportProgressAsync(JobId, _claimToken, report.Percent, report.Message, cancellationToken).AnyContext();
            }
            catch
            {
                lock (_lock)
                    _pending = _pending is { } newer ? newer.Over(report) : report;

                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Progress not stored yet. A newer report's values win; values it leaves out keep the older report's.</summary>
    private readonly record struct ProgressReport(int? Percent, string? Message)
    {
        public ProgressReport Over(ProgressReport? older) => older is { } value ? new ProgressReport(Percent ?? value.Percent, Message ?? value.Message) : this;
    }
}
