using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Nito.AsyncEx;

namespace Foundatio.Caching {
    public class InMemoryCacheClient : ICacheClient {
        private ConcurrentDictionary<string, CacheEntry> _memory;
        private CancellationTokenSource _maintenanceCancellationTokenSource;
        private readonly AsyncLock _asyncLock = new AsyncLock();
        private long _hits = 0;
        private long _misses = 0;
        private DateTime? _nextMaintenance;

        public InMemoryCacheClient() {
            _memory = new ConcurrentDictionary<string, CacheEntry>();
            _maintenanceCancellationTokenSource = new CancellationTokenSource();
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
            _maintenanceCancellationTokenSource?.Cancel();
            if (!FlushOnDispose)
                return;

            _memory = new ConcurrentDictionary<string, CacheEntry>();
        }

        private void ScheduleNextMaintenance(DateTime value) {
            Logger.Trace().Message("ScheduleNextMaintenance: value={0}", value).Write();
            if (value == DateTime.MaxValue)
                return;

            if (_nextMaintenance.HasValue && value > _nextMaintenance.Value)
                return;

            _maintenanceCancellationTokenSource?.Cancel();
            _maintenanceCancellationTokenSource = new CancellationTokenSource();
            int delay = Math.Max((int)value.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            _nextMaintenance = value;
            Logger.Trace().Message("Scheduling delayed task: delay={0}", delay).Write();
            Task.Factory.StartNewDelayed(delay, async () => await DoMaintenanceAsync().AnyContext(), _maintenanceCancellationTokenSource.Token).AnyContext();
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

            if (expiredKeys.Count == 0)
                return;

            foreach (var key in expiredKeys) {
                await this.RemoveAsync(key).AnyContext();
                OnItemExpired(key);
                Logger.Trace().Message("Removing expired key: key={0}", key).Write();
            }
        }

        public event EventHandler<string> ItemExpired;

        private void OnItemExpired(string key) {
            ItemExpired?.Invoke(this, key);
        }

        private class CacheEntry {
            private object _cacheValue;
            private static long _instanceCount = 0;
#if DEBUG
            private long _usageCount = 0;
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
            internal long UsageCount { get { return _usageCount; } }
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
                _memory.TryRemove(key, out cacheEntry);
                Interlocked.Increment(ref _misses);
                return Task.FromResult(CacheValue<T>.Null);
            }

            Interlocked.Increment(ref _hits);

            T value;
            var canConvert = cacheEntry.Value.TryCast(out value);
            return Task.FromResult(new CacheValue<T>(value, canConvert));
        }

        public async Task<IDictionary<string, T>> GetAllAsync<T>(IEnumerable<string> keys) {
            var valueMap = new Dictionary<string, T>();
            foreach (var key in keys) {
                var value = await this.GetAsync<T>(key).AnyContext();
                valueMap[key] = value;
            }

            return valueMap;
        }

        public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < DateTime.UtcNow) {
                await this.RemoveAsync(key).AnyContext();
                return false;
            }

            CacheEntry entry;
            if (TryGetValueInternal(key, out entry))
                return false;

            entry = new CacheEntry(value, expiresAt);
            await SetInternalAsync(key, entry).AnyContext();

            return true;
        }

        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            Logger.Trace().Message("Setting cache: key={0}", key).Write();

            DateTime expiresAt = expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.MaxValue;
            if (expiresAt < DateTime.UtcNow) {
                Logger.Warn().Message("Expires at is less than now: key={0}", key).Write();
                await this.RemoveAsync(key).AnyContext();
                return false;
            }

            CacheEntry entry;
            if (!TryGetValueInternal(key, out entry)) {
                entry = new CacheEntry(value, expiresAt);
                await SetInternalAsync(key, entry).AnyContext();
                return true;
            }

            entry.Value = value;
            entry.ExpiresAt = expiresAt;
            ScheduleNextMaintenance(expiresAt);

            return true;
        }

        private bool TryGetValueInternal(string key, out CacheEntry entry) {
            return _memory.TryGetValue(key, out entry);
        }

        private async Task SetInternalAsync(string key, CacheEntry entry) {
            Logger.Trace().Message("Set: key={0}", key).Write();
            if (entry.ExpiresAt < DateTime.UtcNow) {
                await this.RemoveAsync(key).AnyContext();
                return;
            }

            _memory[key] = entry;
            ScheduleNextMaintenance(entry.ExpiresAt);

            if (MaxItems.HasValue && _memory.Count > MaxItems.Value) {
                string oldest = _memory.ToArray().OrderBy(kvp => kvp.Value.LastAccessTicks).ThenBy(kvp => kvp.Value.InstanceNumber).First().Key;
                CacheEntry cacheEntry;
                _memory.TryRemove(oldest, out cacheEntry);
            }
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
            return !(await SetAsync(key, value, expiresIn).AnyContext());
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
            if (!_memory.TryGetValue(key, out value))
                return Task.FromResult<TimeSpan?>(null);

            if (value.ExpiresAt >= DateTime.UtcNow)
                return Task.FromResult<TimeSpan?>(value.ExpiresAt.Subtract(DateTime.UtcNow));

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

            return Task.FromResult(0);
        }
    }
}