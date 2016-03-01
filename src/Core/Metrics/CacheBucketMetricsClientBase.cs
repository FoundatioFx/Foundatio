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
    public abstract class CacheBucketMetricsClientBase : IBufferedMetricsClient, IMetricsClientStats {
        private readonly ConcurrentQueue<MetricEntry> _queue = new ConcurrentQueue<MetricEntry>(); 
        private readonly ConcurrentDictionary<string, AsyncManualResetEvent> _counterEvents = new ConcurrentDictionary<string, AsyncManualResetEvent>();
        private readonly string _prefix;
        private readonly long _dateBase = new DateTime(2015, 1, 1, 0, 0, 0, 0).Ticks;
        private readonly TimeSpan _defaultExpiration = TimeSpan.FromDays(1);
        private readonly Timer _timer;
        private readonly bool _buffered;
        private readonly ICacheClient _cache;
        protected readonly ILogger _logger;

        public CacheBucketMetricsClientBase(ICacheClient cache, bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger(GetType());
            _cache = cache;
            _buffered = buffered;
            _prefix = !String.IsNullOrEmpty(prefix) ? (!prefix.EndsWith(":") ? prefix + ":" : prefix) : String.Empty;

            if (buffered)
                _timer = new Timer(OnMetricsTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        public Task CounterAsync(string name, int value = 1) {
            if (!_buffered)
                return SubmitCounterAsync(name, GetMinutesSinceBase(), value);

            _queue.Enqueue(new MetricEntry { Name = name, Minute = GetMinutesSinceBase(), Type = MetricType.Counter, Counter = value });

            return TaskHelper.Completed();
        }

        public Task GaugeAsync(string name, double value) {
            if (!_buffered)
                return SubmitGaugeAsync(name, GetMinutesSinceBase(), value);

            _queue.Enqueue(new MetricEntry { Name = name, Minute = GetMinutesSinceBase(), Type = MetricType.Gauge, Gauge = value });

            return TaskHelper.Completed();
        }

        public Task TimerAsync(string name, int milliseconds) {
            if (!_buffered)
                return SubmitTimingAsync(name, GetMinutesSinceBase(), milliseconds);

            _queue.Enqueue(new MetricEntry { Name = name, Minute = GetMinutesSinceBase(), Type = MetricType.Timing, Timing = milliseconds });

            return TaskHelper.Completed();
        }

        private void OnMetricsTimer(object state) {
            FlushAsync().GetAwaiter().GetResult();
        }

        private bool _sendingMetrics = false;
        public async Task FlushAsync() {
            if (_sendingMetrics || _queue.IsEmpty)
                return;

            _logger.Trace("Flushing metrics: count={count}", _queue.Count);

            try {
                _sendingMetrics = true;

                var startTime = DateTime.UtcNow;
                var entries = new List<MetricEntry>();
                MetricEntry entry;
                while (_queue.TryDequeue(out entry)) {
                    entries.Add(entry);
                    if (entry.EnqueuedDate > startTime)
                        break;
                }

                if (entries.Count == 0)
                    return;

                _logger.Trace("Dequeued {count} metrics", entries.Count);

                // counters

                var counters = entries.Where(e => e.Type == MetricType.Counter)
                    .GroupBy(e => new MetricKey(e.Minute, e.Name))
                    .Select(e => new { e.Key.Name, e.Key.Minute, Count = e.Sum(c => c.Counter) }).ToList();

                _logger.Trace("Aggregated {count} counters", counters.Count);

                foreach (var counter in counters)
                    await SubmitCounterAsync(counter.Name, counter.Minute, counter.Count).AnyContext();

                // gauges

                var gauges = entries.Where(e => e.Type == MetricType.Gauge)
                    .GroupBy(e => new MetricKey(e.Minute, e.Name))
                    .Select(e => new { e.Key.Name, e.Key.Minute, Last = e.Last().Gauge, Max = e.Max(c => c.Gauge) }).ToList();

                _logger.Trace("Aggregated {count} gauges", gauges.Count);

                foreach (var gauge in gauges)
                    await SubmitGaugeAsync(gauge.Name, gauge.Minute, gauge.Last, gauge.Max).AnyContext();

                // timings

                var timings = entries.Where(e => e.Type == MetricType.Timing)
                    .GroupBy(e => new MetricKey(e.Minute, e.Name))
                    .Select(e => new { e.Key.Name, e.Key.Minute, Count = e.Count(), Total = e.Sum(c => c.Timing), Min = e.Min(c => c.Timing), Max = e.Max(c => c.Timing) }).ToList();

                _logger.Trace("Aggregated {count} timings", timings.Count);

                foreach (var timing in timings)
                    await SubmitTimingAsync(timing.Name, timing.Minute, timing.Count, timing.Total, timing.Max, timing.Min).AnyContext();
            } finally {
                _sendingMetrics = false;
            }
        }

        private async Task SubmitCounterAsync(string name, long minute, int value) {
            string key = GetBucketKey(MetricNames.Counter, name, minute);
            await _cache.IncrementAsync(key, value, _defaultExpiration).AnyContext();

            AsyncManualResetEvent waitHandle;
            _counterEvents.TryGetValue(name, out waitHandle);
            waitHandle?.Set();
        }

        private Task SubmitGaugeAsync(string name, long minute, double value) {
            return SubmitGaugeAsync(name, minute, value, value);
        }

        private async Task SubmitGaugeAsync(string name, long minute, double last, double max) {
            string lastKey = GetBucketKey(MetricNames.Gauge, name, minute, 1, MetricNames.Last);
            await _cache.SetAsync(lastKey, last, _defaultExpiration).AnyContext();

            string maxKey = GetBucketKey(MetricNames.Gauge, name, minute, 1, MetricNames.Max);
            await _cache.SetIfHigherAsync(maxKey, max, _defaultExpiration).AnyContext();
        }

        public Task SubmitTimingAsync(string name, long minute, int duration) {
            return SubmitTimingAsync(name, minute, 1, duration, duration, duration);
        }

        public async Task SubmitTimingAsync(string name, long minute, int count, int totalDuration, int maxDuration, int minDuration) {
            string countKey = GetBucketKey(MetricNames.Timing, name, minute, 1, MetricNames.Count);
            await _cache.IncrementAsync(countKey, count, _defaultExpiration).AnyContext();

            string totalDurationKey = GetBucketKey(MetricNames.Timing, name, minute, 1, MetricNames.Total);
            await _cache.IncrementAsync(totalDurationKey, totalDuration, _defaultExpiration).AnyContext();

            string maxKey = GetBucketKey(MetricNames.Timing, name, minute, 1, MetricNames.Max);
            await _cache.SetIfHigherAsync(maxKey, maxDuration, _defaultExpiration).AnyContext();

            string minKey = GetBucketKey(MetricNames.Timing, name, minute, 1, MetricNames.Min);
            await _cache.SetIfLowerAsync(minKey, minDuration, _defaultExpiration).AnyContext();
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

            _logger.Trace("Wait: count={count} current={startingCount}", count, startingCount);

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

                currentCount = await this.GetCounterCountAsync(statName, start, DateTime.UtcNow).AnyContext();
                _logger.Trace("Got signal: count={currentCount} expected={expectedCount}", currentCount, expectedCount);

                resetEvent.Reset();
            } while (cancellationToken.IsCancellationRequested == false && currentCount < expectedCount);

            currentCount = await this.GetCounterCountAsync(statName, start, DateTime.UtcNow).AnyContext();
            _logger.Trace("Done waiting: count={currentCount} expected={expectedCount} success={isCancellationRequested}", currentCount, expectedCount, !cancellationToken.IsCancellationRequested);

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

            return new CounterStatSummary(name, stats, start.Value, end.Value);
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

        private class MetricEntry {
            public DateTime EnqueuedDate { get; } = DateTime.UtcNow;
            public string Name { get; set; }
            public long Minute { get; set; }
            public MetricType Type { get; set; }
            public int Counter { get; set; }
            public double Gauge { get; set; }
            public int Timing { get; set; }
        }

        private enum MetricType {
            Counter,
            Gauge,
            Timing
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
