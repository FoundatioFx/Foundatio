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

namespace Foundatio.Caching {
    public class InMemoryCacheClient : MaintenanceBase, ICacheClient {
        private readonly ConcurrentDictionary<string, CacheEntry> _memory;
        private long _hits;
        private long _misses;

        public InMemoryCacheClient(ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            ShouldCloneValues = true;
            _memory = new ConcurrentDictionary<string, CacheEntry>();
            InitializeMaintenance();
        }

        public int Count => _memory.Count;
        public int? MaxItems { get; set; }
        public bool ShouldCloneValues { get; set; }
        public long Hits => _hits;
        public long Misses => _misses;

        public AsyncEvent<ItemExpiredEventArgs> ItemExpired { get; } = new AsyncEvent<ItemExpiredEventArgs>();

        protected virtual async Task OnItemExpiredAsync(string key) {
            var args = new ItemExpiredEventArgs {
                Client = this,
                Key = key
            };

            await (ItemExpired?.InvokeAsync(this, args) ?? TaskHelper.Completed).AnyContext();
        }

        public ICollection<string> Keys {
            get {
                return _memory.ToArray()
                        .OrderBy(kvp => kvp.Value.LastAccessTicks)
                        .ThenBy(kvp => kvp.Value.InstanceNumber)
                        .Select(kvp => kvp.Key)
                        .ToList();
            }
        }

        public Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            if (keys == null) {
                var count = _memory.Count;
                _memory.Clear();
                return Task.FromResult(count);
            }

            if (!keys.Any())
                return TaskHelper.FromResult(0);

            int removed = 0;
            foreach (var key in keys) {
                if (String.IsNullOrEmpty(key))
                    continue;

                _logger.Trace("RemoveAllAsync: Removing key {0}", key);

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
                _logger.Error(ex, "Error trying to remove items from cache with this {0} prefix", prefix);
            }

            return RemoveAllAsync(keysToRemove);
        }

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            CacheEntry cacheEntry;
            if (!_memory.TryGetValue(key, out cacheEntry)) {
                Interlocked.Increment(ref _misses);
                return CacheValue<T>.NoValue;
            }

            if (cacheEntry.ExpiresAt < DateTime.UtcNow) {
                _logger.Trace("TryGetAsync: Removing expired key {0}", key);

                _memory.TryRemove(key, out cacheEntry);
                await OnItemExpiredAsync(key).AnyContext();
                Interlocked.Increment(ref _misses);
                return CacheValue<T>.NoValue;
            }

            Interlocked.Increment(ref _hits);

            try {
                T value = cacheEntry.GetValue<T>();
                return new CacheValue<T>(value, true);
            } catch (Exception ex) {
                _logger.Error(ex, "Unable to deserialize value \"{0}\" to type {1}", cacheEntry.Value, typeof(T).FullName);
                return CacheValue<T>.NoValue;
            }
        }

        public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            var valueMap = new Dictionary<string, CacheValue<T>>();
            foreach (var key in keys)
                valueMap[key] = await GetAsync<T>(key).AnyContext();

            return valueMap;
        }

        public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, ShouldCloneValues), true);
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            // TODO: Look up the existing expiration if expiresIn is null.
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, ShouldCloneValues));
        }

        public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                await OnItemExpiredAsync(key).AnyContext();
                return -1;
            }

            double difference = value;
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            var result = _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, ShouldCloneValues), (k, entry) => {
                long? currentValue = null;
                try {
                    currentValue = entry.GetValue<long?>();
                } catch (Exception ex) {
                    _logger.Error(ex, "Unable to increment value, expected integer type.");
                }

                if (currentValue.HasValue && currentValue.Value < value) {
                    difference = value - currentValue.Value;
                    entry.Value = value;
                } else
                    difference = 0;

                if (expiresIn.HasValue)
                    entry.ExpiresAt = expiresAt;

                return entry;
            });

            if (expiresIn.HasValue)
                ScheduleNextMaintenance(expiresAt);

            return difference;
        }

        public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                await OnItemExpiredAsync(key).AnyContext();
                return -1;
            }

            double difference = value;
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            var result = _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, ShouldCloneValues), (k, entry) => {
                long? currentValue = null;
                try {
                    currentValue = entry.GetValue<long?>();
                } catch (Exception ex) {
                    _logger.Error(ex, "Unable to increment value, expected integer type.");
                }

                if (currentValue.HasValue && currentValue.Value > value) {
                    difference = currentValue.Value - value;
                    entry.Value = value;
                } else
                    difference = 0;

                if (expiresIn.HasValue)
                    entry.ExpiresAt = expiresAt;

                return entry;
            });

            if (expiresIn.HasValue)
                ScheduleNextMaintenance(expiresAt);

            return difference;
        }

        private async Task<bool> SetInternalAsync(string key, CacheEntry entry, bool addOnly = false) {
            if (entry.ExpiresAt < DateTime.UtcNow) {
                _logger.Trace("SetInternalAsync: Removing expired key {0}", key);

                await this.RemoveAsync(key).AnyContext();
                await OnItemExpiredAsync(key).AnyContext();
                return false;
            }

            if (addOnly) {
                if (!_memory.TryAdd(key, entry)) {
                    CacheEntry existingEntry;
                    if (!_memory.TryGetValue(key, out existingEntry) || existingEntry.ExpiresAt >= DateTime.UtcNow)
                        return false;

                    _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                }

                _logger.Trace("Added cache key: {key}", key);
            } else {
                _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                _logger.Trace("Set cache key: {0}", key);
            }

            ScheduleNextMaintenance(entry.ExpiresAt);

            if (MaxItems.HasValue && _memory.Count > MaxItems.Value) {
                string oldest =
                    _memory.ToArray()
                        .OrderBy(kvp => kvp.Value.LastAccessTicks)
                        .ThenBy(kvp => kvp.Value.InstanceNumber)
                        .First()
                        .Key;

                _logger.Trace("SetInternalAsync: Removing key {key}", key);

                CacheEntry cacheEntry;
                _memory.TryRemove(oldest, out cacheEntry);
            }

            return true;
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null || values.Count == 0)
                return 0;

            var result = 0;
            foreach (var entry in values)
                if (await SetAsync(entry.Key, entry.Value).AnyContext())
                    result++;

            return result;
        }

        public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (!_memory.ContainsKey(key))
                return Task.FromResult(false);

            return SetAsync(key, value, expiresIn);
        }

        public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                await OnItemExpiredAsync(key).AnyContext();
                return -1;
            }
            
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            var result = _memory.AddOrUpdate(key, new CacheEntry(amount, expiresAt, ShouldCloneValues), (k, entry) => {
                double? currentValue = null;
                try {
                    currentValue = entry.GetValue<double?>();
                } catch (Exception ex) {
                    _logger.Error(ex, "Unable to increment value, expected integer type.");
                }

                if (currentValue.HasValue)
                    entry.Value = currentValue.Value + amount;
                else
                    entry.Value = amount;

                if (expiresIn.HasValue)
                    entry.ExpiresAt = expiresAt;

                return entry;
            });

            if (expiresIn.HasValue)
                ScheduleNextMaintenance(expiresAt);

            return result.GetValue<double>();
        }
        
        public Task<bool> ExistsAsync(string key) {
            return Task.FromResult(_memory.ContainsKey(key));
        }

        public async Task<TimeSpan?> GetExpirationAsync(string key) {
            CacheEntry value;
            if (!_memory.TryGetValue(key, out value) || value.ExpiresAt == DateTime.MaxValue)
                return null;

            if (value.ExpiresAt >= DateTime.UtcNow)
                return value.ExpiresAt.Subtract(DateTime.UtcNow);

            _logger.Trace("GetExpirationAsync: Removing expired key {key}", key);

            _memory.TryRemove(key, out value);
            await OnItemExpiredAsync(key).AnyContext();
            return null;
        }

        public async Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            DateTime expiresAt = DateTime.UtcNow.Add(expiresIn);
            if (expiresAt < DateTime.UtcNow) {
                await this.RemoveAsync(key).AnyContext();
                await OnItemExpiredAsync(key).AnyContext();
                return;
            }

            CacheEntry value;
            if (_memory.TryGetValue(key, out value)) {
                value.ExpiresAt = expiresAt;
                ScheduleNextMaintenance(expiresAt);
            }
        }
        
        protected override async Task<DateTime> DoMaintenanceAsync() {
            var expiredKeys = new List<string>();

            DateTime utcNow = DateTime.UtcNow;
            DateTime minExpiration = DateTime.MaxValue;

            var enumerator = _memory.GetEnumerator();
            try {
                while (enumerator.MoveNext()) {
                    var current = enumerator.Current;

                    var expiresAt = current.Value.ExpiresAt;
                    if (expiresAt <= utcNow)
                        expiredKeys.Add(current.Key);
                    else if (expiresAt < minExpiration)
                        minExpiration = expiresAt;
                }
            } catch (Exception ex) {
                _logger.Error(ex, "Error trying to find expired cache items.");
            }

            foreach (var key in expiredKeys) {
                _logger.Trace("Removing expired key: key={key}", key);

                await this.RemoveAsync(key).AnyContext();
                await OnItemExpiredAsync(key).AnyContext();
            }

            return minExpiration;
        }
        
        private class CacheEntry {
            private object _cacheValue;
            private static long _instanceCount;
            private readonly bool _shouldClone;
#if DEBUG
            private long _usageCount;
#endif

            public CacheEntry(object value, DateTime expiresAt, bool shouldClone = true) {
                _shouldClone = shouldClone && TypeRequiresCloning(value?.GetType());
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
                    return _shouldClone ? _cacheValue.Copy() : _cacheValue;
                }
                set {
                    _cacheValue = _shouldClone ? value.Copy() : value;
                    LastAccessTicks = DateTime.UtcNow.Ticks;
                    LastModifiedTicks = DateTime.UtcNow.Ticks;
                }
            }

            public T GetValue<T>() {
                var val = Value;
                if (typeof(T) == typeof(Int16) || typeof(T) == typeof(Int32) || typeof(T) == typeof(Int64) ||
                    typeof(T) == typeof(bool) || typeof(T) == typeof(double))
                    return (T)Convert.ChangeType(val, typeof(T));

                if (typeof(T) == typeof(Int16?) || typeof(T) == typeof(Int32?) || typeof(T) == typeof(Int64?) ||
                    typeof(T) == typeof(bool?) || typeof(T) == typeof(double?))
                    return val == null ? default(T) : (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(typeof(T)));

                return (T)val;
            }

            private bool TypeRequiresCloning(Type t) {
                if (t == null)
                    return true;

                if (t == typeof(Int16) || t == typeof(Int32) || t == typeof(Int64) ||
                    t == typeof(bool) || t == typeof(double) || t == typeof(string) ||
                    t == typeof(Int16?) || t == typeof(Int32?) || t == typeof(Int64?) ||
                    t == typeof(bool?) || t == typeof(double?))
                    return false;

                return !t.IsValueType;
            }
        }
    }

    public class ItemExpiredEventArgs : EventArgs {
        public InMemoryCacheClient Client { get; set; }
        public string Key { get; set; }
    }
}