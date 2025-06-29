using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Foundatio.Utility.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Lock;

public class ThrottlingLockProvider : ILockProvider, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider
{
    private readonly ICacheClient _cacheClient;
    private readonly TimeSpan _throttlingPeriod = TimeSpan.FromMinutes(15);
    private readonly int _maxHitsPerPeriod;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IResiliencePolicyProvider _resiliencePolicyProvider;
    private readonly TimeProvider _timeProvider;

    public ThrottlingLockProvider(ICacheClient cacheClient, int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null, TimeProvider timeProvider = null, IResiliencePolicyProvider resiliencePolicyProvider = null, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? cacheClient.GetTimeProvider() ?? TimeProvider.System;
        _resiliencePolicyProvider = resiliencePolicyProvider ?? cacheClient.GetResiliencePolicyProvider();
        _loggerFactory = loggerFactory ?? cacheClient.GetLoggerFactory() ?? NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger<ThrottlingLockProvider>();
        _cacheClient = new ScopedCacheClient(cacheClient, "lock:throttled");
        _maxHitsPerPeriod = maxHitsPerPeriod;

        if (maxHitsPerPeriod <= 0)
            throw new ArgumentException("Must be a positive number.", nameof(maxHitsPerPeriod));

        if (throttlingPeriod.HasValue)
            _throttlingPeriod = throttlingPeriod.Value;
    }

    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    public async Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("AcquireLockAsync: {Resource}", resource);

        bool allowLock = false;
        byte errors = 0;

        string lockId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        do
        {
            string cacheKey = GetCacheKey(resource, _timeProvider.GetUtcNow().UtcDateTime);

            try
            {
                _logger.LogTrace("Current time: {CurrentTime} throttle: {ThrottlingPeriod} key: {Key}", _timeProvider.GetUtcNow().ToString("mm:ss.fff"), _timeProvider.GetUtcNow().UtcDateTime.Floor(_throttlingPeriod).ToString("mm:ss.fff"), cacheKey);
                long? hitCount = await _cacheClient.GetAsync<long?>(cacheKey, 0).AnyContext();

                _logger.LogTrace("Current hit count: {HitCount} max: {MaxHitsPerPeriod}", hitCount, _maxHitsPerPeriod);
                if (hitCount <= _maxHitsPerPeriod - 1)
                {
                    // keep the cache key around for a bit longer than the throttling period
                    var expirationDate = _timeProvider.GetUtcNow().UtcDateTime.Ceiling(_throttlingPeriod).AddMinutes(5);
                    hitCount = await _cacheClient.IncrementAsync(cacheKey, 1, expirationDate).AnyContext();

                    // make sure someone didn't beat us to it.
                    if (hitCount <= _maxHitsPerPeriod)
                    {
                        allowLock = true;
                        break;
                    }

                    _logger.LogTrace("Max hits exceeded after increment for {Resource}", resource);
                }
                else
                {
                    _logger.LogTrace("Max hits exceeded for {Resource}", resource);
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                var sleepUntil = _timeProvider.GetUtcNow().UtcDateTime.Ceiling(_throttlingPeriod).AddMilliseconds(1);
                if (sleepUntil > _timeProvider.GetUtcNow())
                {
                    _logger.LogTrace("Sleeping until key expires: {SleepUntil}", sleepUntil - _timeProvider.GetUtcNow());
                    await _timeProvider.SafeDelay(sleepUntil - _timeProvider.GetUtcNow(), cancellationToken).AnyContext();
                }
                else
                {
                    _logger.LogTrace("Default sleep");
                    await _timeProvider.SafeDelay(TimeSpan.FromMilliseconds(50), cancellationToken).AnyContext();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring throttled lock ({Resource}): {Message}", resource, ex.Message);
                errors++;
                if (errors >= 3)
                    break;

                await _timeProvider.SafeDelay(TimeSpan.FromMilliseconds(50), cancellationToken).AnyContext();
            }
        } while (!cancellationToken.IsCancellationRequested);

        if (cancellationToken.IsCancellationRequested)
            _logger.LogTrace("Cancellation requested");

        if (!allowLock)
            return null;

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

    public Task ReleaseAsync(string resource)
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
