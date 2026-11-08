using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Utility;

namespace Foundatio.Jobs;

internal sealed class JobRunContext : IJobContext
{
    private static readonly AsyncLocal<JobRunContext?> CurrentContext = new();
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly IJobStore _store;
    private readonly string _claimToken;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;
    private (int? Percent, string? Message)? _pending;

    public JobRunContext(JobInfo job, IJobStore store, TimeProvider timeProvider)
    {
        JobId = job.Id;
        JobName = job.Name;
        Attempt = job.Attempt;
        CorrelationId = job.CorrelationId;
        ScheduledFor = job.AvailableAt;
        ScheduleName = job.ScheduleName;
        _store = store;
        _claimToken = job.ClaimToken!;
        _timeProvider = timeProvider;
    }

    public string JobId { get; }
    public string JobName { get; }
    public int Attempt { get; }
    public string? CorrelationId { get; }
    public DateTimeOffset ScheduledFor { get; }
    public string? ScheduleName { get; }

    /// <summary>The job running on this async flow, used to propagate the correlation id.</summary>
    public static JobRunContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    public async Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        if (percent is { } value)
            ArgumentOutOfRangeException.ThrowIfNotEqual(Math.Clamp(value, 0, 100), value, nameof(percent));

        var now = _timeProvider.GetUtcNow();
        if (now - _lastReport < ProgressInterval && percent != 100)
        {
            _pending = (percent ?? _pending?.Percent, message ?? _pending?.Message);
            return;
        }

        _pending = null;
        _lastReport = now;
        await _store.ReportProgressAsync(JobId, _claimToken, percent, message, cancellationToken).AnyContext();
    }

    /// <summary>Writes a throttled progress report that has not been stored yet.</summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_pending is not { } pending)
            return Task.CompletedTask;

        _pending = null;
        return _store.ReportProgressAsync(JobId, _claimToken, pending.Percent, pending.Message, cancellationToken);
    }
}
