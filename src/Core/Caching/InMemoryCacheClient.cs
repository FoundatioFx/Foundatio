using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Caching {
    public class InMemoryCacheClient : ICacheClient {
        private readonly ConcurrentDictionary<string, CacheEntry> _memory;
        private readonly AsyncLock _asyncLock = new AsyncLock();
        private long _hits;
        private long _misses;
        private DateTime? _nextMaintenance;
        private readonly Timer _maintenanceTimer;

        public InMemoryCacheClient() {
            _memory = new ConcurrentDictionary<string, CacheEntry>();
            _maintenanceTimer = new Timer(async s => await DoMaintenanceAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public bool FlushOnDispose { get; set; }

        public int Count => _memory.Count;

        public int? MaxItems { get; set; }

        public long Hits => _hits;

        public long Misses => _misses;

        public ICollection<string> Keys {
            get { return _memory.ToArray().OrderBy(kvp => kvp.Value.LastAccessTicks).ThenBy(kvp => kvp.Value.InstanceNumber).Select(kvp => kvp.Key).ToList(); }
        }

        public void Dispose() {
            _maintenanceTimer.Dispose();
        }

        private void ScheduleNextMaintenance(DateTime value) {
            Logger.Trace().Message("ScheduleNextMaintenance: value={0}", value.ToString("MM-dd-yyyy mm:ss.fff")).Write();
            if (value == DateTime.MaxValue)
                return;

            if (_nextMaintenance.HasValue && value > _nextMaintenance.Value)
                return;
            
            int delay = Math.Max((int)value.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            _nextMaintenance = value;
            Logger.Trace().Message("Scheduling maintenance: delay={0}", delay).Write();
            _maintenanceTimer.Change(delay, Timeout.Infinite);
        }

        private async Task DoMaintenanceAsync() {
            Logger.Trace().Message("Running DoMaintenance").Write();
            DateTime minExpiration = DateTime.MaxValue;
            var now = DateTime.UtcNow;
            var expiredKeys = new List<string>();
            
            foreach (string key in _memory.Keys) {
                var expiresAt = _memory[key].ExpiresAt;
                if (expiresAt <= now)
                    expiredKeys.Add(key);
                else if (expiresAt < minExpiration)
                    minExpiration = expiresAt;
            }

            ScheduleNextMaintenance(minExpiration);
            
            foreach (var key in expiredKeys) {
                await this.RemoveAsync(key).AnyContext();
                OnItemExpired(key);
                Logger.Trace().Message("Removed expired key: key={0}", key).Write();
            }
        }

        public event EventHandler<string> ItemExpired;

        private void OnItemExpired(string key) {
            ItemExpired?.Invoke(this, key);
        }

        private class CacheEntry {
            private object _cacheValue;
            private static long _instanceCount;
#if DEBUG
            private long _usageCount;
#endif

            public CacheEntry(object value, DateTime expiresAt) {
                Value = value;
                ExpiresAt = expiresAt;
                LastModifiedTicks = DateTime.UtcNow.Ticks;
                InstanceNumber = Interlocked.Increment(ref _instanceCount);
            }

            internal long InstanceNumber { get; private set; }
            internal DateTime ExpiresAt { get; set; }
            internal long LastAccessTicks { get; private set; }
            internal long LastModifiedTicks { get; private set; }
#if DEBUG
            internal long UsageCount => _usageCount;
#endif

            internal object Value {
                get {
                    LastAccessTicks = DateTime.UtcNow.Ticks;
#if DEBUG
                    Interlocked.Increment(ref _usageCount);
#endif
                    return _cacheValue.Copy();
                }
                set {
                    _cacheValue = value.Copy();
                    LastAccessTicks = DateTime.UtcNow.Ticks;
                    LastModifiedTicks = DateTime.UtcNow.Ticks;
                }
            }
        }

        public Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            if (keys == null || !keys.Any()) {
                _memory.Clear();
                return Task.FromResult(0);
            }

            int removed = 0;
            foreach (var key in keys) {
                if (String.IsNullOrEmpty(key))
                    continue;
                
                Logger.Trace().Message($"RemoveAllAsync: Removing key {key}").Write();
                CacheEntry item;
                if (_memory.TryRemove(key, out item))
                    removed++;
            }

            return Task.FromResult(removed);
        }

        public Task<int> RemoveByPrefixAsync(string prefix) {
            var keysToRemove = new List<string>();
            var regex = new Regex(String.Concat(prefix, "*").Replace("*", ".*").Replace("?", ".+"));
            var enumerator = _memory.GetEnumerator();
            try {
                while (enumerator.MoveNext()) {
                    var current = enumerator.Current;
                    if (regex.IsMatch(current.Key) || current.Value.ExpiresAt < DateTime.UtcNow)
                        keysToRemove.Add(current.Key);
                }
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error trying to remove items from cache with this {0} prefix", prefix).Write();
            }

            return RemoveAllAsync(keysToRemove);
        }

        public Task<CacheValue<T>> TryGetAsync<T>(string key) {
            CacheEntry cacheEntry;
            if (!_memory.TryGetValue(key, out cacheEntry)) {
                Interlocked.Increment(ref _misses);
                return Task.FromResult(CacheValue<T>.Null);
            }

            if (cacheEntry.ExpiresAt < DateTime.UtcNow) {
                Logger.Trace().Message($"TryGetAsync: Removing expired key {key}").Write();
                _memory.TryRemove(key, out cacheEntry);
                Interlocked.Increment(ref _misses);
                return Task.FromResult(CacheValue<T>.Null);
            }

            Interlocked.Increment(ref _hits);

            try {
                T value;
                if (typeof(T) == typeof(Int16) || typeof(T) == typeof(Int32) || typeof(T) == typeof(Int64) || typeof(T) == typeof(bool) || typeof(T) == typeof(double))
                    value = (T)Convert.ChangeType(cacheEntry.Value, typeof(T));
                else if (typeof(T) == typeof(Int16?) || typeof(T) == typeof(Int32?) || typeof(T) == typeof(Int64?) || typeof(T) == typeof(bool?) || typeof(T) == typeof(double?))
                    value = cacheEntry.Value == null ? default(T) : (T)Convert.ChangeType(cacheEntry.Value, Nullable.GetUnderlyingType(typeof(T)));
                else
                    value = (T)cacheEntry.Value;

                return Task.FromResult(new CacheValue<T>(value, true));
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message($"Unable to deserialize value \"{cacheEntry.Value}\" to type {typeof(T).FullName}").Write();
                return Task.FromResult(new CacheValue<T>(default(T), false));
            }
        }

        public async Task<IDictionary<string, T>> GetAllAsync<T>(IEnumerable<string> keys) {
            var valueMap = new Dictionary<string, T>();
            foreach (var key in keys) {
                var value = await this.GetAsync<T>(key).AnyContext();
                valueMap[key] = value;
            }

            return valueMap;
        }

        public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt), true);
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt));
        }

        private async Task<bool> SetInternalAsync(string key, CacheEntry entry, bool addOnly = false) {
            if (entry.ExpiresAt < DateTime.UtcNow) {
                Logger.Trace().Message($"SetInternalAsync: Removing expired key {key}").Write();
                await this.RemoveAsync(key).AnyContext();
                return false;
            }

            if (addOnly) {
                if (!_memory.TryAdd(key, entry))
                    return false;

                Logger.Trace().Message("Added cache key: {0}", key).Write();
            } else {
                _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                Logger.Trace().Message("Set cache key: {0}", key).Write();
            }

            ScheduleNextMaintenance(entry.ExpiresAt);

            if (MaxItems.HasValue && _memory.Count > MaxItems.Value) {
                string oldest = _memory.ToArray().OrderBy(kvp => kvp.Value.LastAccessTicks).ThenBy(kvp => kvp.Value.InstanceNumber).First().Key;
                
                Logger.Trace().Message($"SetInternalAsync: Removing key {key}").Write();
                CacheEntry cacheEntry;
                _memory.TryRemove(oldest, out cacheEntry);
            }

            return true;
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null)
                return 0;

            var result = 0;
            foreach (var entry in values)
                if (await SetAsync(entry.Key, entry.Value).AnyContext())
                    result++;

            return result;
        }

        public async Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (!_memory.ContainsKey(key))
                return false;
            
            return await SetAsync(key, value, expiresIn).AnyContext();
        }

        public async Task<long> IncrementAsync(string key, int amount = 1, TimeSpan? expiresIn = null) {
            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                return -1;
            }

            using (await _asyncLock.LockAsync()) {
                if (!_memory.ContainsKey(key)) {
                    if (expiresIn.HasValue)
                        await SetAsync(key, amount, expiresIn.Value).AnyContext();
                    else
                        await SetAsync(key, amount).AnyContext();

                    return amount;
                }

                var current = await this.GetAsync<long>(key).AnyContext();
                if (amount == 0)
                    return current;

                if (expiresIn.HasValue)
                    await SetAsync(key, current += amount, expiresIn.Value).AnyContext();
                else
                    await SetAsync(key, current += amount).AnyContext();

                return current;
            }
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            CacheEntry value;
            if (!_memory.TryGetValue(key, out value) || value.ExpiresAt == DateTime.MaxValue)
                return Task.FromResult<TimeSpan?>(null);
            
            if (value.ExpiresAt >= DateTime.UtcNow)
                return Task.FromResult<TimeSpan?>(value.ExpiresAt.Subtract(DateTime.UtcNow));
            
            Logger.Trace().Message($"GetExpirationAsync: Removing expired key {key}").Write();
            _memory.TryRemove(key, out value);
            return Task.FromResult<TimeSpan?>(null);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            DateTime expiresAt = DateTime.UtcNow.Add(expiresIn);
            if (expiresAt < DateTime.UtcNow)
                return this.RemoveAsync(key);

            CacheEntry value;
            if (_memory.TryGetValue(key, out value)) {
                value.ExpiresAt = expiresAt;
                ScheduleNextMaintenance(expiresAt);
            }

            return TaskHelper.Completed();
        }
    }
}