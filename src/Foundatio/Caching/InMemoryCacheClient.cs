using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Caching {
    public class InMemoryCacheClient : ICacheClient, IDisposable {
        private readonly ConcurrentDictionary<string, CacheEntry> _memory;
        private long _hits;
        private long _misses;
        private readonly ILogger _logger;

        public InMemoryCacheClient() : this(o => o) {}

        public InMemoryCacheClient(InMemoryCacheClientOptions options = null) {
            ShouldCloneValues = true;
            var loggerFactory = options?.LoggerFactory ?? NullLoggerFactory.Instance;
            _logger = loggerFactory.CreateLogger<InMemoryCacheClient>();
            _memory = new ConcurrentDictionary<string, CacheEntry>();
        }

        public InMemoryCacheClient(Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> config)
            : this(config(new InMemoryCacheClientOptionsBuilder()).Build()) { }

        public int Count => _memory.Count;
        public int? MaxItems { get; set; }
        public bool ShouldCloneValues { get; set; }
        public long Hits => _hits;
        public long Misses => _misses;

        public AsyncEvent<ItemExpiredEventArgs> ItemExpired { get; } = new AsyncEvent<ItemExpiredEventArgs>();

        private void OnItemExpiredAsync(string key, bool sendNotification = true) {
            if (ItemExpired == null)
                return;

            Task.Factory.StartNew(state => {
                var args = new ItemExpiredEventArgs {
                    Client = this,
                    Key = key,
                    SendNotification = sendNotification
                };

                return ItemExpired.InvokeAsync(this, args);
            }, this, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
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

        public Task<bool> RemoveAsync(string key) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("RemoveAsync: Removing key: {Key}", key);
            return Task.FromResult(_memory.TryRemove(key, out _));
        }

        public Task<bool> RemoveIfEqualAsync<T>(string key, T expected) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

            bool wasExpectedValue = false;
            bool success = _memory.TryUpdate(key, (k, e) => {
                var currentValue = e.GetValue<T>();
                if (currentValue.Equals(expected)) {
                    e.ExpiresAt = DateTime.MinValue;
                    wasExpectedValue = true;
                }
                
                return e;
            });
            
            success = success && wasExpectedValue;

            StartMaintenance();
            
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

            return Task.FromResult(success);
        }

        public Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            if (keys == null) {
                int count = _memory.Count;
                _memory.Clear();
                return Task.FromResult(count);
            }

            int removed = 0;
            foreach (string key in keys) {
                if (String.IsNullOrEmpty(key))
                    continue;

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("RemoveAllAsync: Removing key: {Key}", key);
                if (_memory.TryRemove(key, out _))
                    removed++;
            }

            return Task.FromResult(removed);
        }

        public Task<int> RemoveByPrefixAsync(string prefix) {
            var keysToRemove = new List<string>();
            var regex = new Regex(String.Concat(prefix, "*").Replace("*", ".*").Replace("?", ".+"));
            try {
                foreach (string key in _memory.Keys.ToList())
                    if (regex.IsMatch(key))
                        keysToRemove.Add(key);
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Error trying to remove items from cache with this {Prefix} prefix", prefix);
            }

            return RemoveAllAsync(keysToRemove);
        }

        internal void RemoveExpiredKey(string key, bool sendNotification = true) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing expired key: {Key}", key);

            if (_memory.TryRemove(key, out _))
                OnItemExpiredAsync(key, sendNotification);
        }

        public Task<CacheValue<T>> GetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<CacheValue<T>>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (!_memory.TryGetValue(key, out var cacheEntry)) {
                Interlocked.Increment(ref _misses);
                return Task.FromResult(CacheValue<T>.NoValue);
            }

            if (cacheEntry.ExpiresAt < Time.UtcNow) {
                RemoveExpiredKey(key);
                Interlocked.Increment(ref _misses);
                return Task.FromResult(CacheValue<T>.NoValue);
            }

            Interlocked.Increment(ref _hits);

            try {
                var value = cacheEntry.GetValue<T>();
                return Task.FromResult(new CacheValue<T>(value, true));
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Unable to deserialize value {Value} to type {TypeFullName}", cacheEntry.Value, typeof(T).FullName);
                return Task.FromResult(CacheValue<T>.NoValue);
            }
        }

        public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            var map = new Dictionary<string, Task<CacheValue<T>>>();
            foreach (string key in keys)
                map[key] = GetAsync<T>(key);

            return Task.WhenAll(map.Values)
                .ContinueWith<IDictionary<string, CacheValue<T>>>(t => 
                    map.ToDictionary(k => k.Key, v => v.Value.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, ShouldCloneValues), true);
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, ShouldCloneValues));
        }

        public Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<double>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (expiresIn?.Ticks < 0) {
                RemoveExpiredKey(key);
                return Task.FromResult<double>(-1);
            }

            double difference = value;
            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, ShouldCloneValues), (k, entry) => {
                double? currentValue = null;
                try {
                    currentValue = entry.GetValue<double?>();
                } catch (Exception ex) {
                    _logger.LogError(ex, "Unable to increment value, expected integer type.");
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

            StartMaintenance();

            return Task.FromResult(difference);
        }

        public Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (expiresIn?.Ticks < 0) {
                RemoveExpiredKey(key);
                return Task.FromResult<long>(-1);
            }

            long difference = value;
            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, ShouldCloneValues), (k, entry) => {
                long? currentValue = null;
                try {
                    currentValue = entry.GetValue<long?>();
                } catch (Exception ex) {
                    _logger.LogError(ex, "Unable to increment value, expected integer type.");
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

            StartMaintenance();

            return Task.FromResult(difference);
        }

        public Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<double>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (expiresIn?.Ticks < 0) {
                RemoveExpiredKey(key);
                return Task.FromResult<double>(-1);
            }

            double difference = value;
            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, ShouldCloneValues), (k, entry) => {
                double? currentValue = null;
                try {
                    currentValue = entry.GetValue<double?>();
                } catch (Exception ex) {
                    _logger.LogError(ex, "Unable to increment value, expected integer type.");
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

            StartMaintenance();

            return Task.FromResult(difference);
        }

        public Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (expiresIn?.Ticks < 0) {
                RemoveExpiredKey(key);
                return Task.FromResult<long>(-1);
            }

            long difference = value;
            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, ShouldCloneValues), (k, entry) => {
                long? currentValue = null;
                try {
                    currentValue = entry.GetValue<long?>();
                } catch (Exception ex) {
                    _logger.LogError(ex, "Unable to increment value, expected integer type.");
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

            StartMaintenance();

            return Task.FromResult(difference);
        }

        public Task<long> SetAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (values == null)
                return Task.FromException<long>(new ArgumentNullException(nameof(values)));

            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < Time.UtcNow) {
                RemoveExpiredKey(key);
                return Task.FromResult<long>(default);
            }

            var items = new HashSet<T>(values);
            var entry = new CacheEntry(items, expiresAt, ShouldCloneValues);
            _memory.AddOrUpdate(key, entry, (k, cacheEntry) => {
                if (!(cacheEntry.Value is ICollection<T> collection))
                    throw new InvalidOperationException($"Unable to add value for key: {key}. Cache value does not contain a set.");

                collection.AddRange(items);
                cacheEntry.Value = collection;
                cacheEntry.ExpiresAt = expiresAt;
                return cacheEntry;
            });

            StartMaintenance();

            return Task.FromResult<long>(items.Count);
        }

        public Task<long> SetRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (values == null)
                return Task.FromException<long>(new ArgumentNullException(nameof(values)));

            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < Time.UtcNow) {
                RemoveExpiredKey(key);
                return Task.FromResult<long>(default);
            }

            var items = new HashSet<T>(values);
            _memory.TryUpdate(key, (k, cacheEntry) => {
                if (cacheEntry.Value is ICollection<T> collection && collection.Count > 0) {
                    foreach (var value in items)
                        collection.Remove(value);

                    cacheEntry.Value = collection;
                }

                cacheEntry.ExpiresAt = expiresAt;
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removed value from set with cache key: {Key}", key);
                return cacheEntry;
            });

            return Task.FromResult<long>(items.Count);
        }

        public Task<CacheValue<ICollection<T>>> GetSetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<CacheValue<ICollection<T>>>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            return GetAsync<ICollection<T>>(key);
        }

        private Task<bool> SetInternalAsync(string key, CacheEntry entry, bool addOnly = false) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "SetInternalAsync: Key cannot be null or empty."));

            if (entry.ExpiresAt < Time.UtcNow) {
                RemoveExpiredKey(key);
                return Task.FromResult(false);
            }

            if (addOnly) {
                if (!_memory.TryAdd(key, entry)) {
                    if (!_memory.TryGetValue(key, out var existingEntry) || existingEntry.ExpiresAt < Time.UtcNow) {
                        if (existingEntry != null)
                            RemoveExpiredKey(key);
                        _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                    } else {
                        return Task.FromResult(false);
                    }
                }

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Added cache key: {Key}", key);
            } else {
                _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Set cache key: {Key}", key);
            }

            StartMaintenance(true);

            return Task.FromResult(true);
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null || values.Count == 0)
                return 0;

            var tasks = new List<Task<bool>>();
            foreach (var entry in values)
                tasks.Add(SetAsync(entry.Key, entry.Value));

            bool[] results = await Task.WhenAll(tasks).AnyContext();
            return results.Count(r => r);
        }

        public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (!_memory.ContainsKey(key))
                return Task.FromResult(false);

            return SetAsync(key, value, expiresIn);
        }

        public Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            bool wasExpectedValue = false;
            bool success = _memory.TryUpdate(key, (k, e) => {
                var currentValue = e.GetValue<T>();
                if (currentValue.Equals(expected)) {
                    e.Value = value;
                    wasExpectedValue = true;
                }
                
                return e;
            });

            success = success && wasExpectedValue;

            StartMaintenance();

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

            return Task.FromResult(success);
        }

        public Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<double>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (expiresIn?.Ticks < 0) {
                RemoveExpiredKey(key);
                return Task.FromResult<double>(-1);
            }

            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            var result = _memory.AddOrUpdate(key, new CacheEntry(amount, expiresAt, ShouldCloneValues), (k, entry) => {
                double? currentValue = null;
                try {
                    currentValue = entry.GetValue<double?>();
                } catch (Exception ex) {
                    _logger.LogError(ex, "Unable to increment value, expected integer type.");
                }

                if (currentValue.HasValue)
                    entry.Value = currentValue.Value + amount;
                else
                    entry.Value = amount;

                if (expiresIn.HasValue)
                    entry.ExpiresAt = expiresAt;

                return entry;
            });

            StartMaintenance();

            return Task.FromResult(result.GetValue<double>());
        }

        public Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<long>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (expiresIn?.Ticks < 0) {
                RemoveExpiredKey(key);
                return Task.FromResult<long>(-1);
            }

            var expiresAt = expiresIn.HasValue ? Time.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            var result = _memory.AddOrUpdate(key, new CacheEntry(amount, expiresAt, ShouldCloneValues), (k, entry) => {
                long? currentValue = null;
                try {
                    currentValue = entry.GetValue<long?>();
                } catch (Exception ex) {
                    _logger.LogError(ex, "Unable to increment value, expected integer type.");
                }

                if (currentValue.HasValue)
                    entry.Value = currentValue.Value + amount;
                else
                    entry.Value = amount;

                if (expiresIn.HasValue)
                    entry.ExpiresAt = expiresAt;

                return entry;
            });

            StartMaintenance();

            return Task.FromResult(result.GetValue<long>());
        }

        public Task<bool> ExistsAsync(string key) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            return Task.FromResult(_memory.ContainsKey(key));
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<TimeSpan?>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            if (!_memory.TryGetValue(key, out var value) || value.ExpiresAt == DateTime.MaxValue)
                return Task.FromResult<TimeSpan?>(null);

            if (value.ExpiresAt >= Time.UtcNow)
                return Task.FromResult<TimeSpan?>(value.ExpiresAt.Subtract(Time.UtcNow));

            RemoveExpiredKey(key);

            return Task.FromResult<TimeSpan?>(null);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            var expiresAt = Time.UtcNow.SafeAdd(expiresIn);
            if (expiresAt < Time.UtcNow) {
                RemoveExpiredKey(key);
                return Task.CompletedTask;
            }

            if (_memory.TryGetValue(key, out var value)) {
                value.ExpiresAt = expiresAt;
                StartMaintenance();
            }

            return Task.CompletedTask;
        }

        private DateTimeOffset _lastMaintenance;

        private void StartMaintenance(bool compactImmediately = false) {
            var now = Time.UtcNow;
            if (compactImmediately)
                Compact();

            if (TimeSpan.FromMilliseconds(100) < now - _lastMaintenance) {
                _lastMaintenance = now;
                Task.Factory.StartNew(state => DoMaintenance(), this,
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }
        }

        private void Compact() {
            if (!MaxItems.HasValue || _memory.Count <= MaxItems.Value)
                return;

            (string Key, long LastAccessTicks, long InstanceNumber) oldest = (null, Int64.MaxValue, 0);
            foreach (var kvp in _memory) {
                if (kvp.Value.LastAccessTicks < oldest.LastAccessTicks
                    || (kvp.Value.LastAccessTicks == oldest.LastAccessTicks && kvp.Value.InstanceNumber < oldest.InstanceNumber))
                    oldest = (kvp.Key, kvp.Value.LastAccessTicks, kvp.Value.InstanceNumber);
            }

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing key: {Key}", oldest);
            _memory.TryRemove(oldest.Key, out var cacheEntry);
            if (cacheEntry != null && cacheEntry.ExpiresAt < Time.UtcNow)
                OnItemExpiredAsync(oldest.Key);

            return;
        }

        private void DoMaintenance() {
            _logger.LogTrace("DoMaintenance");

            var utcNow = Time.UtcNow.AddMilliseconds(50);

            try {
                foreach (var kvp in _memory.ToArray()) {
                    var expiresAt = kvp.Value.ExpiresAt;
                    if (expiresAt <= utcNow)
                        RemoveExpiredKey(kvp.Key);
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Error trying to find expired cache items.");
            }

            Compact();
        }

        public void Dispose() {
            _memory.Clear();
            ItemExpired?.Dispose();
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
                LastModifiedTicks = Time.UtcNow.Ticks;
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
                    LastAccessTicks = Time.UtcNow.Ticks;
#if DEBUG
                    Interlocked.Increment(ref _usageCount);
#endif
                    return _shouldClone ? _cacheValue.DeepClone() : _cacheValue;
                }
                set {
                    _cacheValue = _shouldClone ? value.DeepClone() : value;
                    LastAccessTicks = Time.UtcNow.Ticks;
                    LastModifiedTicks = Time.UtcNow.Ticks;
                }
            }

            public T GetValue<T>() {
                object val = Value;
                var t = typeof(T);

                if (t == TypeHelper.BoolType || t == TypeHelper.StringType || t == TypeHelper.CharType || t == TypeHelper.DateTimeType || t == TypeHelper.ObjectType || t.IsNumeric())
                    return (T)Convert.ChangeType(val, t);

                if (t == TypeHelper.NullableBoolType || t == TypeHelper.NullableCharType || t == TypeHelper.NullableDateTimeType || t.IsNullableNumeric())
                    return val == null ? default : (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(t));

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