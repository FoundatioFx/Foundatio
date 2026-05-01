using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Resilience;
using Foundatio.Utility;
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

    public ThrottlingLockProvider(ICacheClient cacheClient, int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null, TimeProvider? timeProvider = null, IResiliencePolicyProvider? resiliencePolicyProvider = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(cacheClient);

        _timeProvider = timeProvider ?? cacheClient.GetTimeProvider() ?? TimeProvider.System;
        _resiliencePolicyProvider = resiliencePolicyProvider ?? cacheClient.GetResiliencePolicyProvider() ?? DefaultResiliencePolicyProvider.Instance;
        _loggerFactory = loggerFactory ?? cacheClient.GetLoggerFactory() ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ThrottlingLockProvider>();
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

    public Task<ILock?> TryAcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        return TryAcquireCoreAsync(resource, timeUntilExpires, releaseOnDispose, cancellationToken);
    }

    public async Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        var l = await TryAcquireCoreAsync(resource, timeUntilExpires, releaseOnDispose, cancellationToken).AnyContext();
        if (l is null)
            throw new LockAcquisitionTimeoutException(resource);

        return l;
    }

    private async Task<ILock?> TryAcquireCoreAsync(string resource, TimeSpan? timeUntilExpires, bool releaseOnDispose, CancellationToken cancellationToken)
    {
        _logger.LogTrace("TryAcquireAsync: {Resource}", resource);

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

                    // make sure someone didn't beat us to it
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

                // Capture the current period's cache key before sleeping. After the delay we
                // spin-check that the key has actually changed, because Task.Delay (via SafeDelay)
                // can wake up to ~4ms early on Linux due to OS timer resolution (CONFIG_HZ).
                // Without this check, GetCacheKey still returns the old period's key, the outer
                // loop sees the exhausted hit count, and sleeps for another full period — doubling
                // the total wait and exceeding the caller's acquireTimeout.
                string previousCacheKey = cacheKey;
                var sleepUntil = _timeProvider.GetUtcNow().UtcDateTime.Ceiling(_throttlingPeriod).AddMilliseconds(1);
                if (sleepUntil > _timeProvider.GetUtcNow())
                {
                    _logger.LogTrace("Sleeping until key expires: {SleepUntil}", sleepUntil - _timeProvider.GetUtcNow());
                    await _timeProvider.SafeDelay(sleepUntil - _timeProvider.GetUtcNow(), cancellationToken).AnyContext();

                    int spins = 0;
                    while (!cancellationToken.IsCancellationRequested
                           && spins < 100
                           && String.Equals(GetCacheKey(resource, _timeProvider.GetUtcNow().UtcDateTime), previousCacheKey, StringComparison.Ordinal))
                    {
                        spins++;
                        await _timeProvider.SafeDelay(TimeSpan.FromMilliseconds(1), cancellationToken).AnyContext();
                    }

                    if (spins >= 100)
                        _logger.LogWarning("Period boundary spin exceeded 100 iterations for {Resource}; clock may be frozen", resource);
                    else if (spins > 0)
                        _logger.LogTrace("Period boundary spin completed after {Spins} iteration(s) for {Resource}", spins, resource);
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

public interface IThrottlingLockProviderFactory
{
    ILockProvider Create(int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null);
}

public class ThrottlingLockProviderFactory : IThrottlingLockProviderFactory
{
    private readonly ICacheClient _cacheClient;
    private readonly TimeProvider _timeProvider;
    private readonly IResiliencePolicyProvider _resiliencePolicyProvider;
    private readonly ILoggerFactory _loggerFactory;

    public ThrottlingLockProviderFactory(ICacheClient cacheClient, TimeProvider? timeProvider = null,
        IResiliencePolicyProvider? resiliencePolicyProvider = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(cacheClient);

        _cacheClient = cacheClient;
        _timeProvider = timeProvider ?? cacheClient.GetTimeProvider() ?? TimeProvider.System;
        _resiliencePolicyProvider = resiliencePolicyProvider ?? cacheClient.GetResiliencePolicyProvider() ?? DefaultResiliencePolicyProvider.Instance;
        _loggerFactory = loggerFactory ?? cacheClient.GetLoggerFactory() ?? NullLoggerFactory.Instance;
    }

    public ILockProvider Create(int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null)
    {
        return new ThrottlingLockProvider(_cacheClient, maxHitsPerPeriod, throttlingPeriod, _timeProvider,
            _resiliencePolicyProvider, _loggerFactory);
    }
}
