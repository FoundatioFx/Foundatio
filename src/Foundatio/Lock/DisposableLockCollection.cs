using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock;

internal class DisposableLockCollection : ILock
{
    private readonly List<ILock> _locks = new();
    private readonly ILogger _logger;
    private bool _isReleased;
    private int _renewalCount;
    private readonly AsyncLock _lock = new();
    private readonly Stopwatch _duration;

    public DisposableLockCollection(IEnumerable<ILock> locks, string lockId, DateTime acquiredTimeUtc, TimeSpan timeWaitedForLock, ILogger logger)
    {
        if (locks == null)
            throw new ArgumentNullException(nameof(locks));

        _locks.AddRange(locks);
        Resource = String.Join("+", _locks.Select(l => l.Resource));
        LockId = lockId;
        TimeWaitedForLock = timeWaitedForLock;
        AcquiredTimeUtc = acquiredTimeUtc;
        _duration = Stopwatch.StartNew();
        _logger = logger;
    }

    public IReadOnlyCollection<ILock> Locks => _locks.AsReadOnly();
    public string LockId { get; }
    public string Resource { get; }
    public DateTime AcquiredTimeUtc { get; }
    public TimeSpan TimeWaitedForLock { get; }
    public int RenewalCount => _renewalCount;

    public async Task RenewAsync(TimeSpan? lockExtension = null)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Renewing {LockCount} locks {Resource}", _locks.Count, Resource);

        await Task.WhenAll(_locks.Select(l => l.RenewAsync(lockExtension))).AnyContext();
        _renewalCount++;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Renewing {LockCount} locks {Resource}", _locks.Count, Resource);
    }

    public async Task ReleaseAsync()
    {
        if (_isReleased)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_isReleased)
                return;

            _isReleased = true;
            _duration.Stop();

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Releasing {LockCount} locks {Resource} after {Duration:g}", _locks.Count, Resource, _duration.Elapsed);

            await Task.WhenAll(_locks.Select(l => l.ReleaseAsync())).AnyContext();
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        if (isTraceLogLevelEnabled)
            _logger.LogTrace("Disposing {LockCount} locks {Resource}", _locks.Count, Resource);

        try
        {
            await Task.WhenAll(_locks.Select(l => l.ReleaseAsync())).AnyContext();
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Unable to release {LockCount} locks {Resource}", _locks.Count, Resource);
        }

        if (isTraceLogLevelEnabled)
            _logger.LogTrace("Disposed {LockCount} locks {Resource}", _locks.Count, Resource);
    }
}
