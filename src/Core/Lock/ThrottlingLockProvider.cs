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

        public ThrottlingLockProvider(ICacheClient cacheClient, int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null) {
            _cacheClient = cacheClient;
            _maxHitsPerPeriod = maxHitsPerPeriod;

            if (maxHitsPerPeriod <= 0)
                throw new ArgumentException("Must be a positive number.", nameof(maxHitsPerPeriod));
            
            if (throttlingPeriod.HasValue)
                _throttlingPeriod = throttlingPeriod.Value;
        }

        public async Task<IDisposable> AcquireLockAsync(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message("AcquireLock: {0}", name).Write();
            if (!acquireTimeout.HasValue)
                acquireTimeout = TimeSpan.FromMinutes(1);

            var timeoutTime = DateTime.UtcNow.Add(acquireTimeout.Value);
            Logger.Trace().Message("Timeout time: {0}", timeoutTime.ToString("mm:ss.fff")).Write();
            bool allowLock = false;

            do {
                string cacheKey = GetCacheKey(name, DateTime.UtcNow);

                try {
                    Logger.Trace().Message("Current time: {0} throttle: {1}", DateTime.UtcNow.ToString("mm:ss.fff"), DateTime.UtcNow.Floor(_throttlingPeriod).ToString("mm:ss.fff")).Write();
                    var hitCount = await _cacheClient.GetAsync<long?>(cacheKey).AnyContext() ?? 0;
                    Logger.Trace().Message("Current hit count: {0} max: {1}", hitCount, _maxHitsPerPeriod).Write();

                    if (hitCount <= _maxHitsPerPeriod - 1) {
                        hitCount = await _cacheClient.IncrementAsync(cacheKey, 1, DateTime.UtcNow.Ceiling(_throttlingPeriod)).AnyContext();
                        
                        // make sure someone didn't beat us to it.
                        if (hitCount <= _maxHitsPerPeriod) {
                            allowLock = true;
                            break;
                        }

                        Logger.Trace().Message("Max hits exceeded after increment for {0}.", name).Write();
                    } else {
                        Logger.Trace().Message("Max hits exceeded for {0}.", name).Write();
                    }
                    
                    if (DateTime.UtcNow > timeoutTime) {
                        Logger.Trace().Message("Timeout exceeded.").Write();
                        break;
                    }

                    var sleepUntil = DateTime.UtcNow.Ceiling(_throttlingPeriod);
                    if (sleepUntil > timeoutTime) {
                        Logger.Trace().Message("Next period is too far away.").Write();
                        await Task.Delay(timeoutTime - DateTime.UtcNow, cancellationToken).AnyContext();
                        break;
                    }
                    
                    if (sleepUntil > DateTime.UtcNow) {
                        Logger.Trace().Message("Sleeping until key expires: {0}", sleepUntil - DateTime.UtcNow).Write();
                        await Task.Delay(sleepUntil - DateTime.UtcNow, cancellationToken).AnyContext();
                    } else {
                        Logger.Trace().Message("Default sleep.").Write();
                        await Task.Delay((int)(acquireTimeout.Value.TotalMilliseconds / 10), cancellationToken).AnyContext();
                    }
                } catch (TaskCanceledException) {
                    return null;
                } catch (Exception ex) {
                    Logger.Error().Message("Error acquiring throttled lock: name={0} message={1}", name, ex.Message).Exception(ex).Write();
                    await Task.Delay((int)(acquireTimeout.Value.TotalMilliseconds / 10), cancellationToken).AnyContext();
                }
            } while (DateTime.UtcNow <= timeoutTime);

            if (!allowLock)
                return null;

            Logger.Trace().Message("Allowing lock: {0}", name).Write();
            return new DisposableLock(name, this);
        }

        public async Task<bool> IsLockedAsync(string name) {
            string cacheKey = GetCacheKey(name, DateTime.UtcNow);
            var hitCount = await _cacheClient.GetAsync<long?>(cacheKey).AnyContext() ?? 0;

            return hitCount >= _maxHitsPerPeriod;
        }

        public Task ReleaseLockAsync(string name) {
            Logger.Trace().Message("ReleaseLockAsync: {0}", name).Write();
            return TaskHelper.Completed();
        }

        private string GetCacheKey(string name, DateTime now) {
            return String.Concat("throttling-lock:", name, ":", now.Floor(_throttlingPeriod).Ticks);
        }

        public void Dispose() {}
    }
}
