using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        private async Task OnItemExpiredAsync(string key, bool sendNotification = true) {
            var args = new ItemExpiredEventArgs {
                Client = this,
                Key = key,
                SendNotification = sendNotification
            };

            await (ItemExpired?.InvokeAsync(this, args) ?? Task.CompletedTask).AnyContext();
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
            try {
                foreach (var key in _memory.Keys.ToList())
                    if (regex.IsMatch(key))
                        keysToRemove.Add(key);
            } catch (Exception ex) {
                _logger.Error(ex, "Error trying to remove items from cache with this {0} prefix", prefix);
            }

            return RemoveAllAsync(keysToRemove);
        }
        
        internal Task RemoveExpiredKeyAsync(string key, bool sendNotification = true) {
            _logger.Trace("Removing expired key {0}", key);

            CacheEntry cacheEntry;
            if (_memory.TryRemove(key, out cacheEntry))
                return OnItemExpiredAsync(key, sendNotification);

            return Task.CompletedTask;
        }

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            CacheEntry cacheEntry;
            if (!_memory.TryGetValue(key, out cacheEntry)) {
                Interlocked.Increment(ref _misses);
                return CacheValue<T>.NoValue;
            }

            if (cacheEntry.ExpiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
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
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            DateTime expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, ShouldCloneValues), true);
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            // TODO: Look up the existing expiration if expiresIn is null.
            DateTime expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, ShouldCloneValues));
        }

        public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            double difference = value;
            DateTime expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
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

            await CleanupAsync().AnyContext();

            return difference;
        }
        
        public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            double difference = value;
            DateTime expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
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

            await CleanupAsync().AnyContext();

            return difference;
        }

        public async Task<bool> SetAddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            // TODO: Look up the existing expiration if expiresIn is null.
            DateTime expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return false;
            }
            
            var entry = new CacheEntry(new List<T> { value }, expiresAt, ShouldCloneValues);
            _memory.AddOrUpdate(key, entry, (k, cacheEntry) => {
                var collection = cacheEntry.Value as ICollection<T>;
                if (collection == null)
                    throw new InvalidOperationException($"Unable to add value for key: {key}. Cache value does not contain a set.");

                collection.Add(value);
                cacheEntry.Value = collection;
                cacheEntry.ExpiresAt = expiresAt;
                return cacheEntry;
            });

            ScheduleNextMaintenance(expiresAt);
            await CleanupAsync().AnyContext();

            return true;
        }

        private async Task CleanupAsync() {
            if (!MaxItems.HasValue || _memory.Count <= MaxItems.Value)
                return;

            string oldest = _memory.ToArray()
                                   .OrderBy(kvp => kvp.Value.LastAccessTicks)
                                   .ThenBy(kvp => kvp.Value.InstanceNumber)
                                   .First()
                                   .Key;

            _logger.Trace("Removing key {oldest}", oldest);

            CacheEntry cacheEntry;
            _memory.TryRemove(oldest, out cacheEntry);
            if (cacheEntry != null && cacheEntry.ExpiresAt < SystemClock.UtcNow)
                await OnItemExpiredAsync(oldest).AnyContext();
        }

        public async Task<bool> SetRemoveAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            DateTime expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return false;
            }
            
            _memory.TryUpdate(key, (k, cacheEntry) => {
                var collection = cacheEntry.Value as ICollection<T>;
                if (collection != null && collection.Contains(value)) {
                    collection.Remove(value);
                    cacheEntry.Value = collection;
                }

                cacheEntry.ExpiresAt = expiresAt;
                _logger.Trace("Removed value from set with cache key: {key}", key);
                return cacheEntry;
            });

            return true;
        }

        public Task<CacheValue<ICollection<T>>> GetSetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            return GetAsync<ICollection<T>>(key);
        }

        private async Task<bool> SetInternalAsync(string key, CacheEntry entry, bool addOnly = false) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("SetInternalAsync: Key cannot be null or empty.");

            if (entry.ExpiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return false;
            }

            if (addOnly) {
                if (!_memory.TryAdd(key, entry)) {
                    CacheEntry existingEntry;
                    if (!_memory.TryGetValue(key, out existingEntry) || existingEntry.ExpiresAt >= SystemClock.UtcNow)
                        return false;

                    _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                }

                _logger.Trace("Added cache key: {key}", key);
            } else {
                _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                _logger.Trace("Set cache key: {0}", key);
            }

            ScheduleNextMaintenance(entry.ExpiresAt);
            await CleanupAsync().AnyContext();

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
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            if (!_memory.ContainsKey(key))
                return Task.FromResult(false);

            return SetAsync(key, value, expiresIn);
        }

        public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }
            
            DateTime expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
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
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            return Task.FromResult(_memory.ContainsKey(key));
        }

        public async Task<TimeSpan?> GetExpirationAsync(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            CacheEntry value;
            if (!_memory.TryGetValue(key, out value) || value.ExpiresAt == DateTime.MaxValue)
                return null;

            if (value.ExpiresAt >= SystemClock.UtcNow)
                return value.ExpiresAt.Subtract(SystemClock.UtcNow);

            await RemoveExpiredKeyAsync(key).AnyContext();
            return null;
        }

        public async Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentException("Key cannot be null or empty.");

            DateTime expiresAt = SystemClock.UtcNow.Add(expiresIn);
            if (expiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return;
            }

            CacheEntry value;
            if (_memory.TryGetValue(key, out value)) {
                value.ExpiresAt = expiresAt;
                ScheduleNextMaintenance(expiresAt);
            }
        }
        
        public override void Dispose() {
            base.Dispose();
            _memory.Clear();
            ItemExpired?.Dispose();
        }

        protected override async Task<DateTime?> DoMaintenanceAsync() {
            _logger.Trace("DoMaintenanceAsync");
            var expiredKeys = new List<string>();

            DateTime utcNow = SystemClock.UtcNow.AddMilliseconds(50);
            DateTime minExpiration = DateTime.MaxValue;
            
            try {
                foreach (var kvp in _memory) {
                    var expiresAt = kvp.Value.ExpiresAt;
                    if (expiresAt <= utcNow)
                        expiredKeys.Add(kvp.Key);
                    else if (expiresAt < minExpiration)
                        minExpiration = expiresAt;
                }
            } catch (Exception ex) {
                _logger.Error(ex, "Error trying to find expired cache items.");
            }

            foreach (var key in expiredKeys)
                await RemoveExpiredKeyAsync(key).AnyContext();

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
                LastModifiedTicks = SystemClock.UtcNow.Ticks;
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
                    LastAccessTicks = SystemClock.UtcNow.Ticks;
#if DEBUG
                    Interlocked.Increment(ref _usageCount);
#endif
                    return _shouldClone ? _cacheValue.Copy() : _cacheValue;
                }
                set {
                    _cacheValue = _shouldClone ? value.Copy() : value;
                    LastAccessTicks = SystemClock.UtcNow.Ticks;
                    LastModifiedTicks = SystemClock.UtcNow.Ticks;
                }
            }

            public T GetValue<T>() {
                object val = Value;
                Type t = typeof(T);
                
                if (t == TypeHelper.BoolType || t == TypeHelper.StringType || t == TypeHelper.CharType || t == TypeHelper.DateTimeType || t == TypeHelper.ObjectType || t.IsNumeric())
                    return (T)Convert.ChangeType(val, t);

                if (t == TypeHelper.NullableBoolType || t == TypeHelper.NullableCharType || t == TypeHelper.NullableDateTimeType || t.IsNullableNumeric())
                    return val == null ? default(T) : (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(t));

                return (T)val;
            }

            private bool TypeRequiresCloning(Type t) {
                if (t == null)
                    return true;

                if (t == TypeHelper.BoolType ||
                    t == TypeHelper.NullableBoolType || 
                    t == TypeHelper.StringType || 
                    t == TypeHelper.CharType || 
                    t == TypeHelper.NullableCharType || 
                    t.IsNumeric() || 
                    t.IsNullableNumeric())
                    return false;

                return !t.GetTypeInfo().IsValueType;
            }
        }
    }

    public class ItemExpiredEventArgs : EventArgs {
        public InMemoryCacheClient Client { get; set; }
        public string Key { get; set; }
        public bool SendNotification { get; set; }
    }
}