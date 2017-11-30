using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Lock {
    public class ThrottlingLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;
        private readonly TimeSpan _throttlingPeriod = TimeSpan.FromMinutes(15);
        private readonly int _maxHitsPerPeriod;
        private readonly ILogger _logger;

        public ThrottlingLockProvider(ICacheClient cacheClient, int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<ThrottlingLockProvider>() ?? NullLogger<ThrottlingLockProvider>.Instance;
            _cacheClient = new ScopedCacheClient(cacheClient, "lock:throttled");
            _maxHitsPerPeriod = maxHitsPerPeriod;

            if (maxHitsPerPeriod <= 0)
                throw new ArgumentException("Must be a positive number.", nameof(maxHitsPerPeriod));

            if (throttlingPeriod.HasValue)
                _throttlingPeriod = throttlingPeriod.Value;
        }

        public async Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled) _logger.LogTrace("AcquireLockAsync: {Name}", name);

            bool allowLock = false;
            byte errors = 0;

            do {
                string cacheKey = GetCacheKey(name, SystemClock.UtcNow);

                try {
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Current time: {CurrentTime} throttle: {ThrottlingPeriod} key: {Key}", SystemClock.UtcNow.ToString("mm:ss.fff"), SystemClock.UtcNow.Floor(_throttlingPeriod).ToString("mm:ss.fff"), cacheKey);
                    var hitCount = await _cacheClient.GetAsync<long?>(cacheKey, 0).AnyContext();

                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Current hit count: {HitCount} max: {MaxHitsPerPeriod}", hitCount, _maxHitsPerPeriod);
                    if (hitCount <= _maxHitsPerPeriod - 1) {
                        hitCount = await _cacheClient.IncrementAsync(cacheKey, 1, SystemClock.UtcNow.Ceiling(_throttlingPeriod)).AnyContext();

                        // make sure someone didn't beat us to it.
                        if (hitCount <= _maxHitsPerPeriod) {
                            allowLock = true;
                            break;
                        }

                        if (isTraceLogLevelEnabled) _logger.LogTrace("Max hits exceeded after increment for {Name}.", name);
                    } else if (isTraceLogLevelEnabled) {
                        _logger.LogTrace("Max hits exceeded for {Name}.", name);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var sleepUntil = SystemClock.UtcNow.Ceiling(_throttlingPeriod).AddMilliseconds(1);
                    if (sleepUntil > SystemClock.UtcNow) {
                        if (isTraceLogLevelEnabled) _logger.LogTrace("Sleeping until key expires: {SleepUntil}", sleepUntil - SystemClock.UtcNow);
                        await SystemClock.SleepAsync(sleepUntil - SystemClock.UtcNow, cancellationToken).AnyContext();
                    } else {
                        if (isTraceLogLevelEnabled) _logger.LogTrace("Default sleep.");
                        await SystemClock.SleepAsync(50, cancellationToken).AnyContext();
                    }
                } catch (OperationCanceledException) {
                    return null;
                } catch (Exception ex) {
                    _logger.LogError(ex, "Error acquiring throttled lock: name={Name} message={Message}", name, ex.Message);
                    errors++;
                    if (errors >= 3)
                        break;

                    await SystemClock.SleepAsync(50, cancellationToken).AnyContext();
                }
            } while (!cancellationToken.IsCancellationRequested);

            if (cancellationToken.IsCancellationRequested && isTraceLogLevelEnabled)
                _logger.LogTrace("Cancellation requested.");

            if (!allowLock)
                return null;

            if (isTraceLogLevelEnabled) _logger.LogTrace("Allowing lock: {Name}", name);
            return new DisposableLock(name, this, _logger);
        }

        public async Task<bool> IsLockedAsync(string name) {
            string cacheKey = GetCacheKey(name, SystemClock.UtcNow);
            long hitCount = await _cacheClient.GetAsync<long>(cacheKey, 0).AnyContext();
            return hitCount >= _maxHitsPerPeriod;
        }

        public Task ReleaseAsync(string name) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("ReleaseAsync: {Name}", name);
            return Task.CompletedTask;
        }
        public Task RenewAsync(string name, TimeSpan? lockExtension = null) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("RenewAsync: {Name}", name);
            return Task.CompletedTask;
        }

        private string GetCacheKey(string name, DateTime now) {
            return String.Concat(name, ":", now.Floor(_throttlingPeriod).Ticks);
        }
    }
}
