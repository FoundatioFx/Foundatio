using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Lock;

public class ThrottlingLockProvider : ILockProvider, IHaveLogger, IHaveTimeProvider
{
    private readonly ICacheClient _cacheClient;
    private readonly TimeSpan _throttlingPeriod = TimeSpan.FromMinutes(15);
    private readonly int _maxHitsPerPeriod;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public ThrottlingLockProvider(ICacheClient cacheClient, int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null, TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? cacheClient.GetTimeProvider();
        _logger = loggerFactory?.CreateLogger<ThrottlingLockProvider>() ?? NullLogger<ThrottlingLockProvider>.Instance;
        _cacheClient = new ScopedCacheClient(cacheClient, "lock:throttled");
        _maxHitsPerPeriod = maxHitsPerPeriod;

        if (maxHitsPerPeriod <= 0)
            throw new ArgumentException("Must be a positive number.", nameof(maxHitsPerPeriod));

        if (throttlingPeriod.HasValue)
            _throttlingPeriod = throttlingPeriod.Value;
    }

    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    public async Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        if (isTraceLogLevelEnabled) _logger.LogTrace("AcquireLockAsync: {Resource}", resource);

        bool allowLock = false;
        byte errors = 0;

        string lockId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        do
        {
            string cacheKey = GetCacheKey(resource, _timeProvider.GetUtcNow().UtcDateTime);

            try
            {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Current time: {CurrentTime} throttle: {ThrottlingPeriod} key: {Key}", _timeProvider.GetUtcNow().ToString("mm:ss.fff"), _timeProvider.GetUtcNow().UtcDateTime.Floor(_throttlingPeriod).ToString("mm:ss.fff"), cacheKey);
                var hitCount = await _cacheClient.GetAsync<long?>(cacheKey, 0).AnyContext();

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Current hit count: {HitCount} max: {MaxHitsPerPeriod}", hitCount, _maxHitsPerPeriod);
                if (hitCount <= _maxHitsPerPeriod - 1)
                {
                    hitCount = await _cacheClient.IncrementAsync(cacheKey, 1, _timeProvider.GetUtcNow().UtcDateTime.Ceiling(_throttlingPeriod)).AnyContext();

                    // make sure someone didn't beat us to it.
                    if (hitCount <= _maxHitsPerPeriod)
                    {
                        allowLock = true;
                        break;
                    }

                    if (isTraceLogLevelEnabled) _logger.LogTrace("Max hits exceeded after increment for {Resource}.", resource);
                }
                else if (isTraceLogLevelEnabled)
                {
                    _logger.LogTrace("Max hits exceeded for {Resource}.", resource);
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                var sleepUntil = _timeProvider.GetUtcNow().UtcDateTime.Ceiling(_throttlingPeriod).AddMilliseconds(1);
                if (sleepUntil > _timeProvider.GetUtcNow())
                {
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Sleeping until key expires: {SleepUntil}", sleepUntil - _timeProvider.GetUtcNow());
                    await _timeProvider.SafeDelay(sleepUntil - _timeProvider.GetUtcNow(), cancellationToken).AnyContext();
                }
                else
                {
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Default sleep");
                    await _timeProvider.SafeDelay(TimeSpan.FromMilliseconds(50), cancellationToken).AnyContext();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring throttled lock: name={Resource} message={Message}", resource, ex.Message);
                errors++;
                if (errors >= 3)
                    break;

                await _timeProvider.SafeDelay(TimeSpan.FromMilliseconds(50), cancellationToken).AnyContext();
            }
        } while (!cancellationToken.IsCancellationRequested);

        if (cancellationToken.IsCancellationRequested && isTraceLogLevelEnabled)
            _logger.LogTrace("Cancellation requested");

        if (!allowLock)
            return null;

        if (isTraceLogLevelEnabled)
            _logger.LogTrace("Allowing lock: {Resource}", resource);

        sw.Stop();
        return new DisposableLock(resource, lockId, sw.Elapsed, this, _logger, releaseOnDispose);
    }

    public async Task<bool> IsLockedAsync(string resource)
    {
        string cacheKey = GetCacheKey(resource, _timeProvider.GetUtcNow().UtcDateTime);
        long hitCount = await _cacheClient.GetAsync<long>(cacheKey, 0).AnyContext();
        return hitCount >= _maxHitsPerPeriod;
    }

    public Task ReleaseAsync(string resource, string lockId)
    {
        return Task.CompletedTask;
    }

    public Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null)
    {
        return Task.CompletedTask;
    }

    private string GetCacheKey(string resource, DateTime now)
    {
        return String.Concat(resource, ":", now.Floor(_throttlingPeriod).Ticks);
    }
}
