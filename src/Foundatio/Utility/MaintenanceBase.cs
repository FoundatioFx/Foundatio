using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility;

public class MaintenanceBase : IDisposable
{
    private ScheduledTimer _maintenanceTimer;
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly TimeProvider _timeProvider;
    protected readonly ILogger _logger;
    private readonly CancellationTokenSource _disposedCancellationTokenSource = new();
    private bool _isDisposed;

    public MaintenanceBase(TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());
    }

    /// <summary>
    /// Gets a cancellation token that is canceled when this instance is disposed.
    /// Use this token to cancel background operations during shutdown.
    /// </summary>
    protected CancellationToken DisposedCancellationToken => _disposedCancellationTokenSource.Token;

    protected void InitializeMaintenance(TimeSpan? dueTime = null, TimeSpan? intervalTime = null)
    {
        _maintenanceTimer = new ScheduledTimer(DoMaintenanceAsync, dueTime, intervalTime, _timeProvider, _loggerFactory);
    }

    protected void ScheduleNextMaintenance(DateTime utcDate)
    {
        _maintenanceTimer.ScheduleNext(utcDate);
    }

    protected virtual Task<DateTime?> DoMaintenanceAsync()
    {
        return Task.FromResult<DateTime?>(DateTime.MaxValue);
    }

    public virtual void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _disposedCancellationTokenSource.Cancel();
        _disposedCancellationTokenSource.Dispose();
        _maintenanceTimer?.Dispose();
    }
}
