using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Metrics {
    public abstract class CacheBucketMetricsClientBase : IMetricsClient, IMetricsClientStats {
        private readonly Dictionary<MetricKey, int> _pendingCounters = new Dictionary<MetricKey, int>();
        private readonly Dictionary<MetricKey, GaugeStat> _pendingGauges = new Dictionary<MetricKey, GaugeStat>();
        private readonly Dictionary<MetricKey, TimingStat> _pendingTimings = new Dictionary<MetricKey, TimingStat>();
        private readonly ConcurrentDictionary<string, AsyncManualResetEvent> _counterEvents = new ConcurrentDictionary<string, AsyncManualResetEvent>();
        private readonly object _lock = new object();
        private readonly string _prefix;
        private readonly long _dateBase = new DateTime(2015, 1, 1, 0, 0, 0, 0).Ticks;
        private readonly TimeSpan _defaultExpiration = TimeSpan.FromDays(1);
        private readonly Timer _timer;
        private readonly bool _buffered;
        private readonly ICacheClient _cache;

        public CacheBucketMetricsClientBase(ICacheClient cache, bool buffered = true, string prefix = null) {
            _cache = cache;
            _buffered = buffered;
            _prefix = !String.IsNullOrEmpty(prefix) ? (!prefix.EndsWith(":") ? prefix + ":" : prefix) : String.Empty;
            if (buffered)
                _timer = new Timer(OnMetricsTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public Task CounterAsync(string name, int value = 1) {
            lock (_lock) {
                var key = new MetricKey(GetMinutesSinceBase(), name);
                if (_pendingCounters.ContainsKey(key))
                    _pendingCounters[key] += value;
                else
                    _pendingCounters.Add(key, value);

                return !_buffered ? FlushAsync() : TaskHelper.Completed();
            }
        }

        public Task GaugeAsync(string name, double value) {
            lock (_lock) {
                var key = new MetricKey(GetMinutesSinceBase(), name);
                if (_pendingGauges.ContainsKey(key)) {
                    var v = _pendingGauges[key];
                    v.Last = value;
                    if (v.Max < value)
                        v.Max = value;
                } else
                    _pendingGauges.Add(key, new GaugeStat { Last = value, Max = value });

                return !_buffered ? FlushAsync() : TaskHelper.Completed();
            }
        }

        public Task TimerAsync(string name, int milliseconds) {
            lock (_lock) {
                var key = new MetricKey(GetMinutesSinceBase(), name);
                if (_pendingTimings.ContainsKey(key)) {
                    var v = _pendingTimings[key];
                    v.Count++;
                    v.TotalDuration += milliseconds;
                    if (v.MaxDuration < milliseconds)
                        v.MaxDuration = milliseconds;
                    if (v.MinDuration > milliseconds)
                        v.MinDuration = milliseconds;
                } else
                    _pendingTimings.Add(key, new TimingStat {
                        Count = 1,
                        TotalDuration = milliseconds,
                        MaxDuration = milliseconds,
                        MinDuration = milliseconds
                    });

                return !_buffered ? FlushAsync() : TaskHelper.Completed();
            }
        }

        public Task FlushAsync() {
            return SendMetricsAsync();
        }

        private void OnMetricsTimer(object state) {
            SendMetricsAsync().GetAwaiter().GetResult();
        }

        private bool _sendingMetrics = false;
        private async Task SendMetricsAsync() {
            Logger.Info().Message("Heeyyyyyy").Write();
            if (_sendingMetrics)
                return;

            if (_pendingCounters.Count == 0 && _pendingGauges.Count == 0 && _pendingTimings.Count == 0)
                return;

            try {
                List<KeyValuePair<MetricKey, int>> pendingCounters = null;
                List<KeyValuePair<MetricKey, GaugeStat>> pendingGauges = null;
                List<KeyValuePair<MetricKey, TimingStat>> pendingTimings = null;
                lock (_lock) {
                    if (_sendingMetrics)
                        return;

                    _sendingMetrics = true;
                    pendingCounters = _pendingCounters.ToList();
                    _pendingCounters.Clear();

                    pendingGauges = _pendingGauges.ToList();
                    _pendingGauges.Clear();

                    pendingTimings = _pendingTimings.ToList();
                    _pendingTimings.Clear();
                }

                foreach (var kvp in pendingCounters) {
                    string key = GetBucketKey(MetricNames.Counter, kvp.Key.Name, kvp.Key.Minute);
                    await _cache.IncrementAsync(key, kvp.Value, _defaultExpiration).AnyContext();

                    AsyncManualResetEvent waitHandle;
                    _counterEvents.TryGetValue(kvp.Key.Name, out waitHandle);
                    waitHandle?.Set();
                }

                foreach (var kvp in pendingGauges) {
                    string lastKey = GetBucketKey(MetricNames.Gauge, kvp.Key.Name, kvp.Key.Minute, 1, MetricNames.Last);
                    await _cache.SetAsync(lastKey, kvp.Value.Last, _defaultExpiration).AnyContext();

                    string maxKey = GetBucketKey(MetricNames.Gauge, kvp.Key.Name, kvp.Key.Minute, 1, MetricNames.Max);
                    await _cache.SetIfHigherAsync(maxKey, kvp.Value.Max, _defaultExpiration).AnyContext();
                }

                foreach (var kvp in pendingTimings) {
                    string countKey = GetBucketKey(MetricNames.Timing, kvp.Key.Name, kvp.Key.Minute, 1, MetricNames.Count);
                    await _cache.IncrementAsync(countKey, kvp.Value.Count, _defaultExpiration).AnyContext();

                    string totalDurationKey = GetBucketKey(MetricNames.Timing, kvp.Key.Name, kvp.Key.Minute, 1, MetricNames.Total);
                    await _cache.IncrementAsync(totalDurationKey, (int)kvp.Value.TotalDuration, _defaultExpiration).AnyContext();

                    string maxKey = GetBucketKey(MetricNames.Timing, kvp.Key.Name, kvp.Key.Minute, 1, MetricNames.Max);
                    await _cache.SetIfHigherAsync(maxKey, kvp.Value.MaxDuration, _defaultExpiration).AnyContext();

                    string minKey = GetBucketKey(MetricNames.Timing, kvp.Key.Name, kvp.Key.Minute, 1, MetricNames.Min);
                    await _cache.SetIfLowerAsync(minKey, kvp.Value.MinDuration, _defaultExpiration).AnyContext();
                }
            } finally {
                _sendingMetrics = false;
            }
        }

        public Task<bool> WaitForCounterAsync(string statName, long count = 1, TimeSpan? timeout = null) {
            return WaitForCounterAsync(statName, TaskHelper.Completed, count, timeout.ToCancellationToken(TimeSpan.FromSeconds(10)));
        }

        public async Task<bool> WaitForCounterAsync(string statName, Func<Task> work, long count = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (count <= 0)
                return true;

            DateTime start = DateTime.UtcNow;
            long startingCount = await this.GetCounterCountAsync(statName, start, start).AnyContext();
            long expectedCount = startingCount + count;
#if DEBUG
            Logger.Trace().Message($"Wait: count={count} current={startingCount}").Write();
#endif
            if (work != null)
                await work().AnyContext();

            long endingCount = await this.GetCounterCountAsync(statName, start, DateTime.UtcNow).AnyContext();
            if (endingCount >= expectedCount)
                return true;

            // TODO: Should we update this to use monitors?
            long currentCount = 0;
            var resetEvent = _counterEvents.GetOrAdd(statName, s => new AsyncManualResetEvent(false));
            do {
                try {
                    await resetEvent.WaitAsync(cancellationToken).AnyContext();
                } catch (OperationCanceledException) { }
#if DEBUG
                currentCount = await this.GetCounterCountAsync(statName, start, DateTime.UtcNow).AnyContext();
                Logger.Trace().Message($"Got signal: count={currentCount} expected={expectedCount}").Write();
#endif
                resetEvent.Reset();
            } while (cancellationToken.IsCancellationRequested == false && currentCount < expectedCount);
#if DEBUG
            currentCount = await this.GetCounterCountAsync(statName, start, DateTime.UtcNow).AnyContext();
            Logger.Trace().Message($"Done waiting: count={currentCount} expected={expectedCount} success={!cancellationToken.IsCancellationRequested}").Write();
#endif
            return !cancellationToken.IsCancellationRequested;
        }

        public async Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? start = null, DateTime? end = null) {
            if (!start.HasValue)
                start = DateTime.UtcNow.AddDays(-1);

            if (!end.HasValue)
                end = DateTime.UtcNow;

            var countBuckets = GetMetricBuckets(MetricNames.Counter, name, start.Value, end.Value, TimeSpan.FromMinutes(1));
            var countResults = await _cache.GetAllAsync<int>(countBuckets.Select(k => k.Key)).AnyContext();

            var stats = new List<CounterStat>();
            for (int i = 0; i < countBuckets.Count; i++) {
                string countKey = countBuckets[i].Key;

                stats.Add(new CounterStat {
                    Time = countBuckets[i].Time,
                    Count = countResults[countKey].Value
                });
            }

            return new CounterStatSummary(stats, start.Value, end.Value);
        }

        public async Task<GaugeStatSummary> GetGaugeStatsAsync(string name, DateTime? start = null, DateTime? end = null) {
            if (!start.HasValue)
                start = DateTime.UtcNow.AddDays(-1);

            if (!end.HasValue)
                end = DateTime.UtcNow;

            var maxBuckets = GetMetricBuckets(MetricNames.Gauge, name, start.Value, end.Value, TimeSpan.FromMinutes(1), MetricNames.Max);
            var lastBuckets = GetMetricBuckets(MetricNames.Gauge, name, start.Value, end.Value, TimeSpan.FromMinutes(1), MetricNames.Last);

            var maxResults = await _cache.GetAllAsync<double>(maxBuckets.Select(k => k.Key)).AnyContext();
            var lastResults = await _cache.GetAllAsync<double>(lastBuckets.Select(k => k.Key)).AnyContext();

            var stats = new List<GaugeStat>();
            for (int i = 0; i < maxBuckets.Count; i++) {
                string maxKey = maxBuckets[i].Key;
                string lastKey = lastBuckets[i].Key;

                stats.Add(new GaugeStat {
                    Time = maxBuckets[i].Time,
                    Max = maxResults[maxKey].Value,
                    Last = lastResults[lastKey].Value
                });
            }

            return new GaugeStatSummary(stats, start.Value, end.Value);
        }

        public async Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? start = null, DateTime? end = null) {
            if (!start.HasValue)
                start = DateTime.UtcNow.AddDays(-1);

            if (!end.HasValue)
                end = DateTime.UtcNow;

            var countBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, TimeSpan.FromMinutes(1), MetricNames.Count);
            var durationBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, TimeSpan.FromMinutes(1), MetricNames.Total);
            var minBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, TimeSpan.FromMinutes(1), MetricNames.Min);
            var maxBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, TimeSpan.FromMinutes(1), MetricNames.Max);

            var countResults = await _cache.GetAllAsync<int>(countBuckets.Select(k => k.Key)).AnyContext();
            var durationResults = await _cache.GetAllAsync<int>(durationBuckets.Select(k => k.Key)).AnyContext();
            var minResults = await _cache.GetAllAsync<int>(minBuckets.Select(k => k.Key)).AnyContext();
            var maxResults = await _cache.GetAllAsync<int>(maxBuckets.Select(k => k.Key)).AnyContext();

            var stats = new List<TimingStat>();
            for (int i = 0; i < countBuckets.Count; i++) {
                string countKey = countBuckets[i].Key;
                string durationKey = durationBuckets[i].Key;
                string minKey = minBuckets[i].Key;
                string maxKey = maxBuckets[i].Key;

                stats.Add(new TimingStat {
                    Time = countBuckets[i].Time,
                    Count = countResults[countKey].Value,
                    TotalDuration = durationResults[durationKey].Value,
                    MinDuration = minResults[minKey].Value,
                    MaxDuration = maxResults[maxKey].Value
                });
            }

            return new TimingStatSummary(stats, start.Value, end.Value);
        }

        private long GetMinutesSinceBase(DateTime? dateTime = null) {
            if (dateTime == null)
                dateTime = DateTime.UtcNow;

            return (dateTime.Value.Ticks - _dateBase) / TimeSpan.TicksPerMinute;
        }

        private string GetBucketKey(string metricType, string statName, long bucket, double intervalMinutes = 1, string suffix = null) {
            suffix = !String.IsNullOrEmpty(suffix) ? ":" + suffix : String.Empty;
            return String.Concat(_prefix, "m:", metricType, ":", statName, ":", intervalMinutes, ":", bucket, suffix);
        }

        private string GetBucketKey(string metricType, string statName, DateTime? dateTime = null, TimeSpan? interval = null, string suffix = null) {
            if (interval == null)
                interval = TimeSpan.FromMinutes(1);

            if (dateTime == null)
                dateTime = DateTime.UtcNow;

            dateTime = dateTime.Value.Floor(interval.Value);
            var bucket = GetMinutesSinceBase(dateTime.Value);

            return GetBucketKey(metricType, statName, bucket, interval.Value.TotalMinutes, suffix);
        }

        private List<MetricBucket> GetMetricBuckets(string metricType, string statName, DateTime start, DateTime end, TimeSpan? interval = null, string suffix = null) {
            if (interval == null)
                interval = TimeSpan.FromMinutes(1);

            start = start.Floor(interval.Value);
            end = end.Floor(interval.Value);

            DateTime current = start;
            var keys = new List<MetricBucket>();
            while (current <= end) {
                keys.Add(new MetricBucket { Key = GetBucketKey(metricType, statName, current, interval, suffix), Time = current });
                current = current.Add(interval.Value);
            }

            return keys;
        }

        public void Dispose() {
            _timer?.Dispose();
            FlushAsync().GetAwaiter().GetResult();
        }

        private class MetricBucket {
            public string Key { get; set; }
            public DateTime Time { get; set; }
        }

        private class MetricNames {
            public const string Counter = "c";
            public const string Gauge = "g";
            public const string Timing = "t";

            public const string Count = "cnt";
            public const string Total = "tot";
            public const string Max = "max";
            public const string Min = "min";
            public const string Last = "last";
        }
    }
}
