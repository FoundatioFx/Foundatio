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
    public class InMemoryCacheClient : ICacheClient {
        private readonly ConcurrentDictionary<string, CacheEntry> _memory;
        private bool _shouldClone;
        private int? _maxItems;
        private long _hits;
        private long _misses;
        private readonly ILogger _logger;

        public InMemoryCacheClient() : this(o => o) {}

        public InMemoryCacheClient(InMemoryCacheClientOptions options = null) {
            if (options == null)
                options = new InMemoryCacheClientOptions();
            _shouldClone = options.CloneValues;
            _maxItems = options.MaxItems;
            var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
            _logger = loggerFactory.CreateLogger<InMemoryCacheClient>();
            _memory = new ConcurrentDictionary<string, CacheEntry>();
        }

        public InMemoryCacheClient(Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> config)
            : this(config(new InMemoryCacheClientOptionsBuilder()).Build()) { }

        public int Count => _memory.Count;
        public int? MaxItems => _maxItems;
        public long Hits => _hits;
        public long Misses => _misses;

        public AsyncEvent<ItemExpiredEventArgs> ItemExpired { get; } = new AsyncEvent<ItemExpiredEventArgs>();

        private Task OnItemExpiredAsync(string key, bool sendNotification = true) {
            if (ItemExpired == null)
                return Task.CompletedTask;

            Task.Factory.StartNew(state => {
                var args = new ItemExpiredEventArgs {
                    Client = this,
                    Key = key,
                    SendNotification = sendNotification
                };

                return ItemExpired.InvokeAsync(this, args);
            }, this, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

            return Task.CompletedTask;
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

        public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

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

            await StartMaintenanceAsync().AnyContext();
            
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("RemoveIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

            return success;
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

        internal Task RemoveExpiredKeyAsync(string key, bool sendNotification = true) {
            _logger.LogDebug("Removing expired cache entry {Key}", key);

            if (_memory.TryRemove(key, out _))
                OnItemExpiredAsync(key, sendNotification);

            return Task.CompletedTask;
        }

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (!_memory.TryGetValue(key, out var cacheEntry)) {
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
                var value = cacheEntry.GetValue<T>();
                return new CacheValue<T>(value, true);
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Unable to deserialize value {Value} to type {TypeFullName}", cacheEntry.Value, typeof(T).FullName);
                return CacheValue<T>.NoValue;
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

            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, _shouldClone), true);
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            return SetInternalAsync(key, new CacheEntry(value, expiresAt, _shouldClone));
        }

        public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            double difference = value;
            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _shouldClone), (k, entry) => {
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

            await StartMaintenanceAsync().AnyContext();

            return difference;
        }

        public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            long difference = value;
            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _shouldClone), (k, entry) => {
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

            await StartMaintenanceAsync().AnyContext();

            return difference;
        }

        public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            double difference = value;
            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _shouldClone), (k, entry) => {
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

            await StartMaintenanceAsync().AnyContext();

            return difference;
        }

        public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            long difference = value;
            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            _memory.AddOrUpdate(key, new CacheEntry(value, expiresAt, _shouldClone), (k, entry) => {
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

            await StartMaintenanceAsync().AnyContext();

            return difference;
        }

        public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return default;
            }

            var items = new HashSet<T>(values);
            var entry = new CacheEntry(items, expiresAt, _shouldClone);
            _memory.AddOrUpdate(key, entry, (k, cacheEntry) => {
                if (!(cacheEntry.Value is ICollection<T> collection))
                    throw new InvalidOperationException($"Unable to add value for key: {key}. Cache value does not contain a set.");

                collection.AddRange(items);
                cacheEntry.Value = collection;
                cacheEntry.ExpiresAt = expiresAt;
                return cacheEntry;
            });

            await StartMaintenanceAsync().AnyContext();

            return items.Count;
        }

        public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return default;
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

            return items.Count;
        }

        public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            var list = await GetAsync<ICollection<T>>(key);
            if (!list.HasValue || !page.HasValue)
                return list;
            
            int skip = (page.Value - 1) * pageSize;
            var pagedItems = list.Value.Skip(skip).Take(pageSize).ToArray();
            return new CacheValue<ICollection<T>>(pagedItems, true);
        }

        private async Task<bool> SetInternalAsync(string key, CacheEntry entry, bool addOnly = false) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "SetInternalAsync: Key cannot be null or empty.");

            if (entry.ExpiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return false;
            }

            if (addOnly) {
                if (!_memory.TryAdd(key, entry)) {
                    if (!_memory.TryGetValue(key, out var existingEntry) || existingEntry.ExpiresAt < SystemClock.UtcNow) {
                        if (existingEntry != null)
                            await RemoveExpiredKeyAsync(key).AnyContext();
                        _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                    } else {
                        return false;
                    }
                }

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Added cache key: {Key}", key);
            } else {
                _memory.AddOrUpdate(key, entry, (k, cacheEntry) => entry);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Set cache key: {Key}", key);
            }

            await StartMaintenanceAsync(true).AnyContext();

            return true;
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

        public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected}", key, expected);

            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
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

            await StartMaintenanceAsync().AnyContext();

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ReplaceIfEqualAsync Key: {Key} Expected: {Expected} Success: {Success}", key, expected, success);

            return success;
        }

        public async Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            var result = _memory.AddOrUpdate(key, new CacheEntry(amount, expiresAt, _shouldClone), (k, entry) => {
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

            await StartMaintenanceAsync().AnyContext();

            return result.GetValue<double>();
        }

        public async Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return -1;
            }

            var expiresAt = expiresIn.HasValue ? SystemClock.UtcNow.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
            var result = _memory.AddOrUpdate(key, new CacheEntry(amount, expiresAt, _shouldClone), (k, entry) => {
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

            await StartMaintenanceAsync().AnyContext();

            return result.GetValue<long>();
        }

        public Task<bool> ExistsAsync(string key) {
            if (String.IsNullOrEmpty(key))
                return Task.FromException<bool>(new ArgumentNullException(nameof(key), "Key cannot be null or empty."));

            return Task.FromResult(_memory.ContainsKey(key));
        }

        public async Task<TimeSpan?> GetExpirationAsync(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (!_memory.TryGetValue(key, out var value) || value.ExpiresAt == DateTime.MaxValue)
                return null;

            if (value.ExpiresAt >= SystemClock.UtcNow)
                return value.ExpiresAt.Subtract(SystemClock.UtcNow);

            await RemoveExpiredKeyAsync(key).AnyContext();

            return null;
        }

        public async Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            var expiresAt = SystemClock.UtcNow.SafeAdd(expiresIn);
            if (expiresAt < SystemClock.UtcNow) {
                await RemoveExpiredKeyAsync(key).AnyContext();
                return;
            }

            if (_memory.TryGetValue(key, out var value)) {
                value.ExpiresAt = expiresAt;
                await StartMaintenanceAsync().AnyContext();
            }
        }

        private DateTimeOffset _lastMaintenance;

        private async Task StartMaintenanceAsync(bool compactImmediately = false) {
            var now = SystemClock.UtcNow;
            if (compactImmediately)
                await CompactAsync().AnyContext();

            if (TimeSpan.FromMilliseconds(100) < now - _lastMaintenance) {
                _lastMaintenance = now;
                var _ = Task.Factory.StartNew(s => DoMaintenanceAsync(), this, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }
        }

        private async Task CompactAsync() {
            if (!_maxItems.HasValue || _memory.Count <= _maxItems)
                return;

            (string Key, long LastAccessTicks, long InstanceNumber) oldest = (null, Int64.MaxValue, 0);
            foreach (var kvp in _memory) {
                if (kvp.Value.LastAccessTicks < oldest.LastAccessTicks
                    || (kvp.Value.LastAccessTicks == oldest.LastAccessTicks && kvp.Value.InstanceNumber < oldest.InstanceNumber))
                    oldest = (kvp.Key, kvp.Value.LastAccessTicks, kvp.Value.InstanceNumber);
            }

            _logger.LogDebug("Removing cache entry {Key} due to cache exceeding max item count limit.", oldest);
            _memory.TryRemove(oldest.Key, out var cacheEntry);
            if (cacheEntry != null && cacheEntry.ExpiresAt < SystemClock.UtcNow)
                await OnItemExpiredAsync(oldest.Key).AnyContext();
        }

        private async Task DoMaintenanceAsync() {
            _logger.LogTrace("DoMaintenance");

            var utcNow = SystemClock.UtcNow.AddMilliseconds(50);

            try {
                foreach (var kvp in _memory.ToArray()) {
                    var expiresAt = kvp.Value.ExpiresAt;
                    if (expiresAt <= utcNow)
                        await RemoveExpiredKeyAsync(kvp.Key).AnyContext();
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Error trying to find expired cache items.");
            }

            await CompactAsync().AnyContext();
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
                    return _shouldClone ? _cacheValue.DeepClone() : _cacheValue;
                }
                set {
                    _cacheValue = _shouldClone ? value.DeepClone() : value;
                    LastAccessTicks = SystemClock.UtcNow.Ticks;
                    LastModifiedTicks = SystemClock.UtcNow.Ticks;
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