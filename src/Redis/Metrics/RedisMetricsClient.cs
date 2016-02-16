using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Metrics;
using Foundatio.Utility;
using StackExchange.Redis;

namespace Foundatio.Redis.Metrics {
    public class RedisMetricsClient : IMetricsClient, IMetricsClientStats {
        private readonly Dictionary<MetricKey, int> _pendingCounters = new Dictionary<MetricKey, int>();
        private readonly Dictionary<MetricKey, GaugeStat> _pendingGauges = new Dictionary<MetricKey, GaugeStat>();
        private readonly Dictionary<MetricKey, TimingStat> _pendingTimings = new Dictionary<MetricKey, TimingStat>();
        private readonly object _lock = new object();
        private readonly string _prefix;
        private readonly long _dateBase = new DateTime(2015, 1, 1, 0, 0, 0, 0).Ticks;
        private readonly TimeSpan _defaultExpiration = TimeSpan.FromDays(1);
        private readonly Timer _timer;
        private readonly RedisCacheClient _cache;

        public RedisMetricsClient(ConnectionMultiplexer connection, string prefix = null) {
            _cache = new RedisCacheClient(connection);
            _prefix = !String.IsNullOrEmpty(prefix) ? (!prefix.EndsWith(":") ? prefix + ":" : prefix) : String.Empty;
            _timer = new Timer(OnMetricsTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public Task CounterAsync(string statName, int value = 1) {
            lock (_lock) {
                var key = new MetricKey(GetMinutesSinceBase(), statName);
                if (_pendingCounters.ContainsKey(key))
                    _pendingCounters[key] += value;
                else
                    _pendingCounters.Add(key, value);
            }

            return TaskHelper.Completed();
        }

        public Task GaugeAsync(string statName, double value) {
            lock (_lock) {
                var key = new MetricKey(GetMinutesSinceBase(), statName);
                if (_pendingGauges.ContainsKey(key)) {
                    var v = _pendingGauges[key];
                    v.Last = value;
                    if (v.Max < value)
                        v.Max = value;
                } else
                    _pendingGauges.Add(key, new GaugeStat { Last = value, Max = value });
            }

            return TaskHelper.Completed();
        }

        public Task TimerAsync(string statName, int milliseconds) {
            lock (_lock) {
                var key = new MetricKey(GetMinutesSinceBase(), statName);
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
            }
            
            return TaskHelper.Completed();
        }

        public Task FlushAsync() {
            return SendMetricsAsync();
        }

        private void OnMetricsTimer(object state) {
            SendMetricsAsync().GetAwaiter().GetResult();
        }

        private bool _sendingMetrics = false;
        private async Task SendMetricsAsync() {
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
                    await _cache.IncrementAsync(key, kvp.Value, _defaultExpiration);
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

        public async Task<CounterStatSummary> GetCounterStatsAsync(string statName, DateTime start, DateTime end) {
            var countBuckets = GetMetricBuckets(MetricNames.Counter, statName, start, end, TimeSpan.FromMinutes(1));
            var countResults = await _cache.GetAllAsync<int>(countBuckets.Select(k => k.Key)).AnyContext();

            var stats = new List<CounterStat>();
            for (int i = 0; i < countBuckets.Count; i++) {
                string countKey = countBuckets[i].Key;

                stats.Add(new CounterStat {
                    Time = countBuckets[i].Time,
                    Count = countResults[countKey].Value
                });
            }

            return new CounterStatSummary(stats, start, end);
        }

        public async Task<GaugeStatSummary> GetGaugeStatsAsync(string statName, DateTime start, DateTime end) {
            var maxBuckets = GetMetricBuckets(MetricNames.Gauge, statName, start, end, TimeSpan.FromMinutes(1), MetricNames.Max);
            var lastBuckets = GetMetricBuckets(MetricNames.Gauge, statName, start, end, TimeSpan.FromMinutes(1), MetricNames.Last);

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

            return new GaugeStatSummary(stats, start, end);
        }

        public async Task<TimingStatSummary> GetTimerStatsAsync(string statName, DateTime start, DateTime end) {
            var countBuckets = GetMetricBuckets(MetricNames.Timing, statName, start, end, TimeSpan.FromMinutes(1), MetricNames.Count);
            var durationBuckets = GetMetricBuckets(MetricNames.Timing, statName, start, end, TimeSpan.FromMinutes(1), MetricNames.Total);
            var minBuckets = GetMetricBuckets(MetricNames.Timing, statName, start, end, TimeSpan.FromMinutes(1), MetricNames.Min);
            var maxBuckets = GetMetricBuckets(MetricNames.Timing, statName, start, end, TimeSpan.FromMinutes(1), MetricNames.Max);

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

            return new TimingStatSummary(stats, start, end);
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
