using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility;

public class MaintenanceBase : IDisposable
{
    private ScheduledTimer? _maintenanceTimer;
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly TimeProvider _timeProvider;
    protected readonly ILogger _logger;
    private readonly CancellationTokenSource _disposedCancellationTokenSource = new();
    private int _disposeState;
    protected bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    public MaintenanceBase(TimeProvider? timeProvider, ILoggerFactory? loggerFactory)
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
        if (_maintenanceTimer is null)
        {
            _logger.LogWarning("ScheduleNextMaintenance called before InitializeMaintenance");
            return;
        }

        _maintenanceTimer.ScheduleNext(utcDate);
    }

    protected virtual Task<DateTime?> DoMaintenanceAsync()
    {
        return Task.FromResult<DateTime?>(DateTime.MaxValue);
    }

    /// <summary>
    /// Signals that this instance is being disposed by canceling the disposal token and setting <see cref="IsDisposed"/>.
    /// Call this early in derived <c>Dispose()</c> methods to signal background tasks before waiting on them.
    /// Calling <see cref="Dispose()"/> after this method is still required to release resources.
    /// </summary>
    /// <returns><c>true</c> if this is the first caller (disposal was signaled); <c>false</c> if already signaled.</returns>
    protected bool SignalDispose()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            return false;

        _disposedCancellationTokenSource.Cancel();
        return true;
    }

    public virtual void Dispose()
    {
        SignalDispose();
        _maintenanceTimer?.Dispose();
        _disposedCancellationTokenSource.Dispose();
    }
}
