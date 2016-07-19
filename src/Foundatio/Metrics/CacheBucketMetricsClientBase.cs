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
        private readonly BucketSettings[] _buckets = {
            new BucketSettings { Size = TimeSpan.FromMinutes(5), Ttl = TimeSpan.FromHours(1) },
            new BucketSettings { Size = TimeSpan.FromHours(1), Ttl = TimeSpan.FromDays(7) }
        };

        private readonly string _prefix;
        private readonly Timer _flushTimer;
        private readonly bool _buffered;
        protected readonly ICacheClient _cache;
        protected readonly ILogger _logger;

        public CacheBucketMetricsClientBase(ICacheClient cache, bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger(GetType());
            _cache = cache;
            _buffered = buffered;
            _prefix = !String.IsNullOrEmpty(prefix) ? (!prefix.EndsWith(":") ? prefix + ":" : prefix) : String.Empty;

            if (buffered)
                _flushTimer = new Timer(OnMetricsTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        public Task CounterAsync(string name, int value = 1) {
            _logger.Trace(() => $"Counter name={name} value={value} buffered={_buffered}");
            var entry = new MetricEntry { Name = name, Type = MetricType.Counter, Counter = value };
            if (!_buffered)
                return SubmitMetricAsync(entry);

            _queue.Enqueue(entry);

            return Task.CompletedTask;
        }

        public Task GaugeAsync(string name, double value) {
            _logger.Trace(() => $"Gauge name={name} value={value} buffered={_buffered}");
            var entry = new MetricEntry { Name = name, Type = MetricType.Gauge, Gauge = value };
            if (!_buffered)
                return SubmitMetricAsync(entry);

            _queue.Enqueue(entry);

            return Task.CompletedTask;
        }

        public Task TimerAsync(string name, int milliseconds) {
            _logger.Trace(() => $"Timer name={name} milliseconds={milliseconds} buffered={_buffered}");
            var entry = new MetricEntry { Name = name, Type = MetricType.Timing, Timing = milliseconds };
            if (!_buffered)
                return SubmitMetricAsync(entry);

            _queue.Enqueue(entry);

            return Task.CompletedTask;
        }
        
        private void OnMetricsTimer(object state) {
            try {
                FlushAsync().GetAwaiter().GetResult();
            } catch (Exception ex) {
                _logger.Error(ex, () => $"Error flushing metrics: {ex.Message}");
            }
        }

        private bool _sendingMetrics = false;
        public async Task FlushAsync() {
            if (_sendingMetrics || _queue.IsEmpty)
                return;

            _logger.Trace("Flushing metrics: count={count}", _queue.Count);

            try {
                _sendingMetrics = true;

                var startTime = SystemClock.UtcNow;
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
                await SubmitMetricsAsync(entries).AnyContext();
            } finally {
                _sendingMetrics = false;
            }
        }

        private Task SubmitMetricAsync(MetricEntry metric) {
            return SubmitMetricsAsync(new List<MetricEntry> { metric });
        }

        private async Task SubmitMetricsAsync(List<MetricEntry> metrics) {
            foreach (var bucket in _buckets) {
                // counters
                var counters = metrics.Where(e => e.Type == MetricType.Counter)
                    .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(bucket.Size), e.Name))
                    .Select(e => new { e.Key.Name, e.Key.Time, Count = e.Sum(c => c.Counter) }).ToList();

                if (metrics.Count > 1)
                    _logger.Trace(() => $"Aggregated {counters.Count} counters");
                if (counters.Count > 0)
                    await Run.WithRetriesAsync(() => Task.WhenAll(counters.Select(c => StoreCounterAsync(c.Name, c.Time, c.Count, bucket)))).AnyContext();

                // gauges
                var gauges = metrics.Where(e => e.Type == MetricType.Gauge)
                .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(bucket.Size), e.Name))
                .Select(e => new { e.Key.Name, Minute = e.Key.Time, Count = e.Count(), Total = e.Sum(c => c.Gauge), Last = e.Last().Gauge, Min = e.Min(c => c.Gauge), Max = e.Max(c => c.Gauge) }).ToList();

                if (metrics.Count > 1)
                    _logger.Trace(() => $"Aggregated {gauges.Count} gauges");
                if (gauges.Count > 0)
                    await Run.WithRetriesAsync(() => Task.WhenAll(gauges.Select(g => StoreGaugeAsync(g.Name, g.Minute, g.Count, g.Total, g.Last, g.Min, g.Max, bucket)))).AnyContext();

                // timings
                var timings = metrics.Where(e => e.Type == MetricType.Timing)
                .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(bucket.Size), e.Name))
                .Select(e => new { e.Key.Name, Minute = e.Key.Time, Count = e.Count(), Total = e.Sum(c => c.Timing), Min = e.Min(c => c.Timing), Max = e.Max(c => c.Timing) }).ToList();

                if (metrics.Count > 1)
                    _logger.Trace(() => $"Aggregated {timings.Count} timings");
                if (timings.Count > 0)
                    await Run.WithRetriesAsync(() => Task.WhenAll(timings.Select(t => StoreTimingAsync(t.Name, t.Minute, t.Count, t.Total, t.Max, t.Min, bucket)))).AnyContext();
            }
        }

        private async Task StoreCounterAsync(string name, DateTime time, int value, BucketSettings settings) {
            _logger.Trace(() => $"Storing counter name={name} value={value} time={time}");

            string key = GetBucketKey(MetricNames.Counter, name, time, settings.Size);
            await _cache.IncrementAsync(key, value, settings.Ttl).AnyContext();

            AsyncManualResetEvent waitHandle;
            _counterEvents.TryGetValue(name, out waitHandle);
            waitHandle?.Set();

            _logger.Trace(() => $"Done storing counter name={name}");
        }

        private Task StoreGaugeAsync(string name, DateTime time, double value, BucketSettings settings) {
            return StoreGaugeAsync(name, time, 1, value, value, value, value, settings);
        }

        private async Task StoreGaugeAsync(string name, DateTime time, int count, double total, double last, double min, double max, BucketSettings settings) {
            _logger.Trace(() => $"Storing gauge name={name} count={count} total={total} last={last} min={min} max={max} time={time}");

            string countKey = GetBucketKey(MetricNames.Timing, name, time, settings.Size, MetricNames.Count);
            await _cache.IncrementAsync(countKey, count, settings.Ttl).AnyContext();

            string totalDurationKey = GetBucketKey(MetricNames.Timing, name, time, settings.Size, MetricNames.Total);
            await _cache.IncrementAsync(totalDurationKey, total, settings.Ttl).AnyContext();

            string lastKey = GetBucketKey(MetricNames.Gauge, name, time, settings.Size, MetricNames.Last);
            await _cache.SetAsync(lastKey, last, settings.Ttl).AnyContext();

            string minKey = GetBucketKey(MetricNames.Timing, name, time, settings.Size, MetricNames.Min);
            await _cache.SetIfLowerAsync(minKey, min, settings.Ttl).AnyContext();

            string maxKey = GetBucketKey(MetricNames.Gauge, name, time, settings.Size, MetricNames.Max);
            await _cache.SetIfHigherAsync(maxKey, max, settings.Ttl).AnyContext();

            _logger.Trace(() => $"Done storing gauge name={name}");
        }

        private Task StoreTimingAsync(string name, DateTime time, int duration, BucketSettings settings) {
            return StoreTimingAsync(name, time, 1, duration, duration, duration, settings);
        }

        private async Task StoreTimingAsync(string name, DateTime time, int count, int totalDuration, int maxDuration, int minDuration, BucketSettings settings) {
            _logger.Trace(() => $"Storing timing name={name} count={count} total={totalDuration} min={minDuration} max={maxDuration} time={time}");

            string countKey = GetBucketKey(MetricNames.Timing, name, time, settings.Size, MetricNames.Count);
            await _cache.IncrementAsync(countKey, count, settings.Ttl).AnyContext();

            string totalDurationKey = GetBucketKey(MetricNames.Timing, name, time, settings.Size, MetricNames.Total);
            await _cache.IncrementAsync(totalDurationKey, totalDuration, settings.Ttl).AnyContext();

            string maxKey = GetBucketKey(MetricNames.Timing, name, time, settings.Size, MetricNames.Max);
            await _cache.SetIfHigherAsync(maxKey, maxDuration, settings.Ttl).AnyContext();

            string minKey = GetBucketKey(MetricNames.Timing, name, time, settings.Size, MetricNames.Min);
            await _cache.SetIfLowerAsync(minKey, minDuration, settings.Ttl).AnyContext();

            _logger.Trace(() => $"Done storing timing name={name}");
        }

        public Task<bool> WaitForCounterAsync(string statName, long count = 1, TimeSpan? timeout = null) {
            return WaitForCounterAsync(statName, () => Task.CompletedTask, count, timeout.ToCancellationToken(TimeSpan.FromSeconds(10)));
        }

        public async Task<bool> WaitForCounterAsync(string statName, Func<Task> work, long count = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (count <= 0)
                return true;

            DateTime start = SystemClock.UtcNow;
            long startingCount = await this.GetCounterCountAsync(statName, start, start).AnyContext();
            long expectedCount = startingCount + count;

            _logger.Trace("Wait: count={count} current={startingCount}", count, startingCount);

            if (work != null)
                await work().AnyContext();

            long endingCount = await this.GetCounterCountAsync(statName, start, SystemClock.UtcNow).AnyContext();
            if (endingCount >= expectedCount)
                return true;

            // TODO: Should we update this to use monitors?
            long currentCount = 0;
            var resetEvent = _counterEvents.GetOrAdd(statName, s => new AsyncManualResetEvent(false));
            do {
                try {
                    await resetEvent.WaitAsync(cancellationToken).AnyContext();
                } catch (OperationCanceledException) {}
                
                currentCount = await this.GetCounterCountAsync(statName, start, SystemClock.UtcNow).AnyContext();
                _logger.Trace("Got signal: count={currentCount} expected={expectedCount}", currentCount, expectedCount);

                resetEvent.Reset();
            } while (cancellationToken.IsCancellationRequested == false && currentCount < expectedCount);

            currentCount = await this.GetCounterCountAsync(statName, start, SystemClock.UtcNow).AnyContext();
            _logger.Trace("Done waiting: count={currentCount} expected={expectedCount} success={isCancellationRequested}", currentCount, expectedCount, !cancellationToken.IsCancellationRequested);

            return !cancellationToken.IsCancellationRequested;
        }

        public async Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20) {
            if (!start.HasValue)
                start = SystemClock.UtcNow.AddHours(-4);

            if (!end.HasValue)
                end = SystemClock.UtcNow;

            var interval = end.Value.Subtract(start.Value).TotalMinutes > 60 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);

            var countBuckets = GetMetricBuckets(MetricNames.Counter, name, start.Value, end.Value, interval);
            var countResults = await _cache.GetAllAsync<int>(countBuckets.Select(k => k.Key)).AnyContext();

            ICollection<CounterStat> stats = new List<CounterStat>();
            for (int i = 0; i < countBuckets.Count; i++) {
                string countKey = countBuckets[i].Key;

                stats.Add(new CounterStat {
                    Time = countBuckets[i].Time,
                    Count = countResults[countKey].Value
                });
            }

            stats = stats.ReduceTimeSeries(s => s.Time, (s, d) => new CounterStat {
                Time = d,
                Count = s.Sum(i => i.Count)
            }, dataPoints);

            return new CounterStatSummary(name, stats, start.Value, end.Value);
        }

        public async Task<GaugeStatSummary> GetGaugeStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20) {
            if (!start.HasValue)
                start = SystemClock.UtcNow.AddHours(-4);

            if (!end.HasValue)
                end = SystemClock.UtcNow;

            var interval = end.Value.Subtract(start.Value).TotalMinutes > 60 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);
            
            var countBuckets = GetMetricBuckets(MetricNames.Gauge, name, start.Value, end.Value, interval, MetricNames.Count);
            var totalBuckets = GetMetricBuckets(MetricNames.Gauge, name, start.Value, end.Value, interval, MetricNames.Total);
            var lastBuckets = GetMetricBuckets(MetricNames.Gauge, name, start.Value, end.Value, interval, MetricNames.Last);
            var minBuckets = GetMetricBuckets(MetricNames.Gauge, name, start.Value, end.Value, interval, MetricNames.Min);
            var maxBuckets = GetMetricBuckets(MetricNames.Gauge, name, start.Value, end.Value, interval, MetricNames.Max);

            var countResults = await _cache.GetAllAsync<int>(countBuckets.Select(k => k.Key)).AnyContext();
            var totalResults = await _cache.GetAllAsync<double>(totalBuckets.Select(k => k.Key)).AnyContext();
            var lastResults = await _cache.GetAllAsync<double>(lastBuckets.Select(k => k.Key)).AnyContext();
            var minResults = await _cache.GetAllAsync<double>(minBuckets.Select(k => k.Key)).AnyContext();
            var maxResults = await _cache.GetAllAsync<double>(maxBuckets.Select(k => k.Key)).AnyContext();

            ICollection<GaugeStat> stats = new List<GaugeStat>();
            for (int i = 0; i < maxBuckets.Count; i++) {
                string countKey = countBuckets[i].Key;
                string totalKey = totalBuckets[i].Key;
                string minKey = minBuckets[i].Key;
                string maxKey = maxBuckets[i].Key;
                string lastKey = lastBuckets[i].Key;

                stats.Add(new GaugeStat {
                    Time = maxBuckets[i].Time,
                    Count = countResults[countKey].Value,
                    Total = totalResults[totalKey].Value,
                    Min = minResults[minKey].Value,
                    Max = maxResults[maxKey].Value,
                    Last = lastResults[lastKey].Value
                });
            }

            stats = stats.ReduceTimeSeries(s => s.Time, (s, d) => new GaugeStat {
                Time = d,
                Count = s.Sum(i => i.Count),
                Total = s.Sum(i => i.Total),
                Min = s.Min(i => i.Min),
                Max = s.Max(i => i.Max),
                Last = s.Last().Last
            }, dataPoints);

            return new GaugeStatSummary(stats, start.Value, end.Value);
        }

        public async Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20) {
            if (!start.HasValue)
                start = SystemClock.UtcNow.AddHours(-4);

            if (!end.HasValue)
                end = SystemClock.UtcNow;

            var interval = end.Value.Subtract(start.Value).TotalMinutes > 60 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);

            var countBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, interval, MetricNames.Count);
            var durationBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, interval, MetricNames.Total);
            var minBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, interval, MetricNames.Min);
            var maxBuckets = GetMetricBuckets(MetricNames.Timing, name, start.Value, end.Value, interval, MetricNames.Max);

            var countResults = await _cache.GetAllAsync<int>(countBuckets.Select(k => k.Key)).AnyContext();
            var durationResults = await _cache.GetAllAsync<int>(durationBuckets.Select(k => k.Key)).AnyContext();
            var minResults = await _cache.GetAllAsync<int>(minBuckets.Select(k => k.Key)).AnyContext();
            var maxResults = await _cache.GetAllAsync<int>(maxBuckets.Select(k => k.Key)).AnyContext();

            ICollection<TimingStat> stats = new List<TimingStat>();
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

            stats = stats.ReduceTimeSeries(s => s.Time, (s, d) => new TimingStat {
                Time = d,
                Count = s.Sum(i => i.Count),
                MinDuration = s.Min(i => i.MinDuration),
                MaxDuration = s.Max(i => i.MaxDuration),
                TotalDuration = s.Sum(i => i.TotalDuration)
            }, dataPoints);

            return new TimingStatSummary(stats, start.Value, end.Value);
        }

        private string GetBucketKey(string metricType, string statName, DateTime? dateTime = null, TimeSpan? interval = null, string suffix = null) {
            if (interval == null)
                interval = _buckets[0].Size;

            if (dateTime == null)
                dateTime = SystemClock.UtcNow;

            dateTime = dateTime.Value.Floor(interval.Value);

            suffix = !String.IsNullOrEmpty(suffix) ? ":" + suffix : String.Empty;
            return String.Concat(_prefix, "m:", metricType, ":", statName, ":", interval.Value.TotalMinutes, ":", dateTime.Value.ToString("yy-MM-dd-hh-mm"), suffix);
        }

        private List<MetricBucket> GetMetricBuckets(string metricType, string statName, DateTime start, DateTime end, TimeSpan? interval = null, string suffix = null) {
            if (interval == null)
                interval = _buckets[0].Size;

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

        public virtual void Dispose() {
            _flushTimer?.Dispose();
            FlushAsync().GetAwaiter().GetResult();
            _queue?.Clear();
            _counterEvents?.Clear();
        }

        private struct BucketSettings {
            public TimeSpan Size { get; set; }
            public TimeSpan Ttl { get; set; }
        }

        private class MetricEntry {
            public DateTime EnqueuedDate { get; } = SystemClock.UtcNow;
            public string Name { get; set; }
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
