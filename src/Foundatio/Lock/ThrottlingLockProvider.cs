using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Lock {
    public class ThrottlingLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;
        private readonly TimeSpan _throttlingPeriod = TimeSpan.FromMinutes(15);
        private readonly int _maxHitsPerPeriod;
        private readonly ILogger _logger;

        public ThrottlingLockProvider(ICacheClient cacheClient, int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger<ThrottlingLockProvider>();
            _cacheClient = new ScopedCacheClient(cacheClient, "lock:throttled");
            _maxHitsPerPeriod = maxHitsPerPeriod;

            if (maxHitsPerPeriod <= 0)
                throw new ArgumentException("Must be a positive number.", nameof(maxHitsPerPeriod));
            
            if (throttlingPeriod.HasValue)
                _throttlingPeriod = throttlingPeriod.Value;
        }

        public async Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Trace("AcquireLockAsync: {name}", name);
			            
            bool allowLock = false;
            byte errors = 0;

            do {
                string cacheKey = GetCacheKey(name, SystemClock.UtcNow);

                try {
                    _logger.Trace("Current time: {0} throttle: {1} key: {2}", SystemClock.UtcNow.ToString("mm:ss.fff"), SystemClock.UtcNow.Floor(_throttlingPeriod).ToString("mm:ss.fff"), cacheKey);
                    var hitCount = await _cacheClient.GetAsync<long?>(cacheKey, 0).AnyContext();

                    _logger.Trace("Current hit count: {0} max: {1}", hitCount, _maxHitsPerPeriod);
                    if (hitCount <= _maxHitsPerPeriod - 1) {
                        hitCount = await _cacheClient.IncrementAsync(cacheKey, 1, SystemClock.UtcNow.Ceiling(_throttlingPeriod)).AnyContext();
                        
                        // make sure someone didn't beat us to it.
                        if (hitCount <= _maxHitsPerPeriod) {
                            allowLock = true;
                            break;
                        }

                        _logger.Trace("Max hits exceeded after increment for {0}.", name);
                    } else {
                        _logger.Trace("Max hits exceeded for {0}.", name);
                    }
                    
                    if (cancellationToken.IsCancellationRequested) {
                        _logger.Trace("Cancellation Requested.");
                        break;
                    }

                    var sleepUntil = SystemClock.UtcNow.Ceiling(_throttlingPeriod).AddMilliseconds(1);
                    if (sleepUntil > SystemClock.UtcNow) {
                        _logger.Trace("Sleeping until key expires: {0}", sleepUntil - SystemClock.UtcNow);
                        await SystemClock.SleepAsync(sleepUntil - SystemClock.UtcNow, cancellationToken).AnyContext();
                    } else {
                        _logger.Trace("Default sleep.");
                        await SystemClock.SleepAsync(50, cancellationToken).AnyContext();
                    }
                } catch (TaskCanceledException) {
                    return null;
                } catch (Exception ex) {
                    _logger.Error(ex, "Error acquiring throttled lock: name={0} message={1}", name, ex.Message);
                    errors++;
                    if (errors >= 3)
                        break;

                    await SystemClock.SleepAsync(50, cancellationToken).AnyContext();
                }
            } while (!cancellationToken.IsCancellationRequested);

            if (cancellationToken.IsCancellationRequested) {
                _logger.Trace("Cancellation requested.");
            }

            if (!allowLock)
                return null;

            _logger.Trace("Allowing lock: {0}", name);
            return new DisposableLock(name, this, _logger);
        }

        public async Task<bool> IsLockedAsync(string name) {
            string cacheKey = GetCacheKey(name, SystemClock.UtcNow);
            var hitCount = await _cacheClient.GetAsync<long>(cacheKey, 0).AnyContext();

            return hitCount >= _maxHitsPerPeriod;
        }

        public Task ReleaseAsync(string name) {
            _logger.Trace("ReleaseAsync: {0}", name);

            return Task.CompletedTask;
        }
        public Task RenewAsync(String name, TimeSpan? lockExtension = null) {
            _logger.Trace("RenewAsync: {0}", name);

            return Task.CompletedTask;
        }

        private string GetCacheKey(string name, DateTime now) {
            return String.Concat(name, ":", now.Floor(_throttlingPeriod).Ticks);
        }
    }
}
