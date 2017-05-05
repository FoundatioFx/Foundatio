using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public abstract class CacheBucketMetricsClientBase : BufferedMetricsClientBase, IMetricsClientStats {
        protected readonly ICacheClient _cache;
        private readonly string _prefix;

        public CacheBucketMetricsClientBase(ICacheClient cache, MetricsClientOptionsBase options) : base(options) {
            _cache = cache;
            _prefix = !String.IsNullOrEmpty(options.Prefix) ? (!options.Prefix.EndsWith(":") ? options.Prefix + ":" : options.Prefix) : String.Empty;

            _timeBuckets.Clear();
            _timeBuckets.Add(new TimeBucket { Size = TimeSpan.FromMinutes(5), Ttl = TimeSpan.FromHours(1) });
            _timeBuckets.Add(new TimeBucket { Size = TimeSpan.FromHours(1), Ttl = TimeSpan.FromDays(7) });
        }

        protected override async Task StoreAggregatedMetricsAsync(TimeBucket timeBucket, ICollection<AggregatedCounterMetric> counters, ICollection<AggregatedGaugeMetric> gauges, ICollection<AggregatedTimingMetric> timings) {
            foreach (var counter in counters)
                await StoreCounterAsync(timeBucket, counter).AnyContext();

            foreach (var gauge in gauges)
                await StoreGaugeAsync(timeBucket, gauge).AnyContext();

            foreach (var timing in timings)
                await StoreTimingAsync(timeBucket, timing).AnyContext();
        }

        private async Task StoreCounterAsync(TimeBucket timeBucket, AggregatedCounterMetric counter) {
            _logger.Trace(() => $"Storing counter name={counter.Key.Name} value={counter.Value} time={counter.Key.Duration}");

            string bucketKey = GetBucketKey(CacheMetricNames.Counter, counter.Key.Name, counter.Key.StartTimeUtc, timeBucket.Size);
            await _cache.IncrementAsync(bucketKey, counter.Value, timeBucket.Ttl).AnyContext();

            _logger.Trace(() => $"Done storing counter name={counter.Key.Name}");
        }

        private async Task StoreGaugeAsync(TimeBucket timeBucket, AggregatedGaugeMetric gauge) {
            _logger.Trace(() => $"Storing gauge name={gauge.Key.Name} count={gauge.Count} total={gauge.Total} last={gauge.Last} min={gauge.Min} max={gauge.Max} time={gauge.Key.StartTimeUtc}");

            string countKey = GetBucketKey(CacheMetricNames.Gauge, gauge.Key.Name, gauge.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Count);
            await _cache.IncrementAsync(countKey, gauge.Count, timeBucket.Ttl).AnyContext();

            string totalDurationKey = GetBucketKey(CacheMetricNames.Gauge, gauge.Key.Name, gauge.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Total);
            await _cache.IncrementAsync(totalDurationKey, gauge.Total, timeBucket.Ttl).AnyContext();

            string lastKey = GetBucketKey(CacheMetricNames.Gauge, gauge.Key.Name, gauge.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Last);
            await _cache.SetAsync(lastKey, gauge.Last, timeBucket.Ttl).AnyContext();

            string minKey = GetBucketKey(CacheMetricNames.Gauge, gauge.Key.Name, gauge.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Min);
            await _cache.SetIfLowerAsync(minKey, gauge.Min, timeBucket.Ttl).AnyContext();

            string maxKey = GetBucketKey(CacheMetricNames.Gauge, gauge.Key.Name, gauge.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Max);
            await _cache.SetIfHigherAsync(maxKey, gauge.Max, timeBucket.Ttl).AnyContext();

            _logger.Trace(() => $"Done storing gauge name={gauge.Key.Name}");
        }

        private async Task StoreTimingAsync(TimeBucket timeBucket, AggregatedTimingMetric timing) {
            _logger.Trace(() => $"Storing timing name={timing.Key.Name} count={timing.Count} total={timing.TotalDuration} min={timing.MinDuration} max={timing.MaxDuration} time={timing.Key.StartTimeUtc}");

            string countKey = GetBucketKey(CacheMetricNames.Timing, timing.Key.Name, timing.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Count);
            await _cache.IncrementAsync(countKey, timing.Count, timeBucket.Ttl).AnyContext();

            string totalDurationKey = GetBucketKey(CacheMetricNames.Timing, timing.Key.Name, timing.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Total);
            await _cache.IncrementAsync(totalDurationKey, timing.TotalDuration, timeBucket.Ttl).AnyContext();

            string maxKey = GetBucketKey(CacheMetricNames.Timing, timing.Key.Name, timing.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Max);
            await _cache.SetIfHigherAsync(maxKey, timing.MaxDuration, timeBucket.Ttl).AnyContext();

            string minKey = GetBucketKey(CacheMetricNames.Timing, timing.Key.Name, timing.Key.StartTimeUtc, timeBucket.Size, CacheMetricNames.Min);
            await _cache.SetIfLowerAsync(minKey, timing.MinDuration, timeBucket.Ttl).AnyContext();

            _logger.Trace(() => $"Done storing timing name={timing.Key.Name}");
        }

        public async Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20) {
            if (!start.HasValue)
                start = SystemClock.UtcNow.AddHours(-4);

            if (!end.HasValue)
                end = SystemClock.UtcNow;

            var interval = end.Value.Subtract(start.Value).TotalMinutes > 180 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);

            var countBuckets = GetMetricBuckets(CacheMetricNames.Counter, name, start.Value, end.Value, interval);
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

            var interval = end.Value.Subtract(start.Value).TotalMinutes > 180 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);

            var countBuckets = GetMetricBuckets(CacheMetricNames.Gauge, name, start.Value, end.Value, interval, CacheMetricNames.Count);
            var totalBuckets = GetMetricBuckets(CacheMetricNames.Gauge, name, start.Value, end.Value, interval, CacheMetricNames.Total);
            var lastBuckets = GetMetricBuckets(CacheMetricNames.Gauge, name, start.Value, end.Value, interval, CacheMetricNames.Last);
            var minBuckets = GetMetricBuckets(CacheMetricNames.Gauge, name, start.Value, end.Value, interval, CacheMetricNames.Min);
            var maxBuckets = GetMetricBuckets(CacheMetricNames.Gauge, name, start.Value, end.Value, interval, CacheMetricNames.Max);

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

            return new GaugeStatSummary(name, stats, start.Value, end.Value);
        }

        public async Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? start = null, DateTime? end = null, int dataPoints = 20) {
            if (!start.HasValue)
                start = SystemClock.UtcNow.AddHours(-4);

            if (!end.HasValue)
                end = SystemClock.UtcNow;

            var interval = end.Value.Subtract(start.Value).TotalMinutes > 180 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);

            var countBuckets = GetMetricBuckets(CacheMetricNames.Timing, name, start.Value, end.Value, interval, CacheMetricNames.Count);
            var durationBuckets = GetMetricBuckets(CacheMetricNames.Timing, name, start.Value, end.Value, interval, CacheMetricNames.Total);
            var minBuckets = GetMetricBuckets(CacheMetricNames.Timing, name, start.Value, end.Value, interval, CacheMetricNames.Min);
            var maxBuckets = GetMetricBuckets(CacheMetricNames.Timing, name, start.Value, end.Value, interval, CacheMetricNames.Max);

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

            return new TimingStatSummary(name, stats, start.Value, end.Value);
        }

        private string GetBucketKey(string metricType, string statName, DateTime? dateTime = null, TimeSpan? interval = null, string suffix = null) {
            if (interval == null)
                interval = _timeBuckets[0].Size;

            if (dateTime == null)
                dateTime = SystemClock.UtcNow;

            dateTime = dateTime.Value.Floor(interval.Value);

            suffix = !String.IsNullOrEmpty(suffix) ? ":" + suffix : String.Empty;
            return String.Concat(_prefix, "m:", metricType, ":", statName, ":", interval.Value.TotalMinutes, ":", dateTime.Value.ToString("yy-MM-dd-hh-mm"), suffix);
        }

        private List<MetricBucket> GetMetricBuckets(string metricType, string statName, DateTime start, DateTime end, TimeSpan? interval = null, string suffix = null) {
            if (interval == null)
                interval = _timeBuckets[0].Size;

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

        private class CacheMetricNames {
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
