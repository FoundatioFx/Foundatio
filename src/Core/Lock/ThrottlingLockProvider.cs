using System;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Utility;
using NLog.Fluent;

namespace Foundatio.Lock {
    public class ThrottlingLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;
        private readonly TimeSpan _throttlingPeriod = TimeSpan.FromMinutes(15);
        private readonly int _maxHitsPerPeriod;

        public ThrottlingLockProvider(ICacheClient cacheClient, int maxHitsPerPeriod = 100, TimeSpan? throttlingPeriod = null) {
            _cacheClient = cacheClient;
            _maxHitsPerPeriod = maxHitsPerPeriod;
            if (throttlingPeriod.HasValue)
                _throttlingPeriod = throttlingPeriod.Value;
        }

        public IDisposable AcquireLock(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            Log.Trace().Message("AcquireLock: {0}", name).Write();
            if (!acquireTimeout.HasValue)
                acquireTimeout = TimeSpan.FromMinutes(1);

            Run.UntilTrue(() => {
                string cacheKey = GetCacheKey(name);

                try {
                    Log.Trace().Message("Current time: {0} throttled: {1}", DateTime.UtcNow.ToString("mm:ss.fff"), DateTime.UtcNow.Floor(_throttlingPeriod).ToString("mm:ss.fff")).Write();
                    var hitCount = _cacheClient.Get<long?>(cacheKey) ?? 0;
                    Log.Trace().Message("Current hit count: {0} max: {1}", hitCount, _maxHitsPerPeriod).Write();

                    if (hitCount > _maxHitsPerPeriod - 1) {
                        Log.Trace().Message("Max hits exceeded for {0}.", name).Write();
                        return false;
                    }

                    _cacheClient.Increment(cacheKey, 1, _throttlingPeriod);
                    return true;
                } catch (Exception ex) {
                    Log.Error().Message("Error incrementing hit count for {0}: {1}", name, ex.Message).Exception(ex).Write();
                    return false;
                }
            }, acquireTimeout, TimeSpan.FromMilliseconds(50));

            Log.Trace().Message("Allowing lock: {0}", name).Write();
            return new DisposableLock(name, this);
        }

        public bool IsLocked(string name) {
            return false;
        }

        public void ReleaseLock(string name) {}

        private string GetCacheKey(string name) {
            return String.Concat("throttling-lock:", name, ":", DateTime.UtcNow.Floor(_throttlingPeriod).Ticks);
        }
    }
}
