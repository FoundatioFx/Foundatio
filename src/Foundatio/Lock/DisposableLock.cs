using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock;

internal class DisposableLock : ILock
{
    private readonly ILockProvider _lockProvider;
    private readonly ILogger _logger;
    private bool _isReleased;
    private int _renewalCount;
    private readonly AsyncLock _lock = new();
    private readonly Stopwatch _duration;
    private readonly bool _shouldReleaseOnDispose;

    public DisposableLock(string resource, string lockId, TimeSpan timeWaitedForLock, ILockProvider lockProvider, ILogger logger, bool shouldReleaseOnDispose)
    {
        Resource = resource;
        LockId = lockId;
        TimeWaitedForLock = timeWaitedForLock;
        AcquiredTimeUtc = lockProvider.GetTimeProvider().GetUtcNow().UtcDateTime;
        _duration = Stopwatch.StartNew();
        _logger = logger;
        _lockProvider = lockProvider;
        _shouldReleaseOnDispose = shouldReleaseOnDispose;
    }

    public string LockId { get; }
    public string Resource { get; }
    public DateTime AcquiredTimeUtc { get; }
    public TimeSpan TimeWaitedForLock { get; }
    public int RenewalCount => _renewalCount;

    public async ValueTask DisposeAsync()
    {
        if (!_shouldReleaseOnDispose)
            return;

        _logger.LogTrace("Disposing lock {Resource} ({LockId})", Resource, LockId);

        try
        {
            await ReleaseAsync().AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to release lock {Resource} ({LockId})", Resource, LockId);
        }

        _logger.LogTrace("Disposed lock {Resource} ({LockId})", Resource, LockId);
    }

    public async Task RenewAsync(TimeSpan? timeUntilExpires = null)
    {
        _logger.LogTrace("Renewing lock {Resource} ({LockId})", Resource, LockId);

        await _lockProvider.RenewAsync(Resource, LockId, timeUntilExpires).AnyContext();
        _renewalCount++;

        _logger.LogDebug("Renewed lock {Resource} ({LockId})", Resource, LockId);
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

            _logger.LogDebug("Releasing lock {Resource} ({LockId}) after {Duration:g}", Resource, LockId, _duration.Elapsed);
            await _lockProvider.ReleaseAsync(Resource, LockId).AnyContext();
            _logger.LogDebug("Released lock {Resource} ({LockId})", Resource, LockId);
        }
    }
}
