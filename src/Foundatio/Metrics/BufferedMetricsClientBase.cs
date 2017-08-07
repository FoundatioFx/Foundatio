﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Utility;
using Foundatio.AsyncEx;

namespace Foundatio.Metrics {
    public abstract class BufferedMetricsClientBase : IBufferedMetricsClient {
        protected readonly List<TimeBucket> _timeBuckets = new List<TimeBucket> {
            new TimeBucket { Size = TimeSpan.FromMinutes(1) }
        };

        private readonly ConcurrentQueue<MetricEntry> _queue = new ConcurrentQueue<MetricEntry>();
        private readonly Timer _flushTimer;
        private readonly MetricsClientOptionsBase _options;
        protected readonly ILogger _logger;

        public BufferedMetricsClientBase(MetricsClientOptionsBase options) {
            _options = options;
            _logger = options.LoggerFactory.CreateLogger(GetType());
            if (options.Buffered)
                _flushTimer = new Timer(OnMetricsTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        public AsyncEvent<CountedEventArgs> Counted { get; } = new AsyncEvent<CountedEventArgs>(true);

        protected virtual Task OnCountedAsync(long value) {
            var counted = Counted;
            if (counted == null)
                return Task.CompletedTask;

            var args = new CountedEventArgs { Value = value };
            return counted.InvokeAsync(this, args);
        }

        public Task CounterAsync(string name, int value = 1) {
            var entry = new MetricEntry { Name = name, Type = MetricType.Counter, Counter = value };
            if (!_options.Buffered)
                return SubmitMetricAsync(entry);

            _queue.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task GaugeAsync(string name, double value) {
            var entry = new MetricEntry { Name = name, Type = MetricType.Gauge, Gauge = value };
            if (!_options.Buffered)
                return SubmitMetricAsync(entry);

            _queue.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task TimerAsync(string name, int milliseconds) {
            var entry = new MetricEntry { Name = name, Type = MetricType.Timing, Timing = milliseconds };
            if (!_options.Buffered)
                return SubmitMetricAsync(entry);

            _queue.Enqueue(entry);
            return Task.CompletedTask;
        }

        private void OnMetricsTimer(object state) {
            try {
                FlushAsync().AnyContext().GetAwaiter().GetResult();
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
                while (_queue.TryDequeue(out MetricEntry entry)) {
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

        protected virtual async Task SubmitMetricsAsync(List<MetricEntry> metrics) {
            foreach (var timeBucket in _timeBuckets) {
                try {
                    // counters
                    var counters = metrics.Where(e => e.Type == MetricType.Counter).ToList();
                    var groupedCounters = counters
                        .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(timeBucket.Size), timeBucket.Size, e.Name))
                        .Select(e => new AggregatedCounterMetric {
                            Key = e.Key,
                            Value = e.Sum(c => c.Counter),
                            Entries = e.ToList()
                        }).ToList();

                    if (metrics.Count > 1 && counters.Count > 0)
                        _logger.Trace(() => $"Aggregated {counters.Count} counter(s) into {groupedCounters.Count} counter group(s)");

                    // gauges
                    var gauges = metrics.Where(e => e.Type == MetricType.Gauge).ToList();
                    var groupedGauges = gauges
                        .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(timeBucket.Size), timeBucket.Size, e.Name))
                        .Select(e => new AggregatedGaugeMetric {
                            Key = e.Key,
                            Count = e.Count(),
                            Total = e.Sum(c => c.Gauge),
                            Last = e.Last().Gauge,
                            Min = e.Min(c => c.Gauge),
                            Max = e.Max(c => c.Gauge),
                            Entries = e.ToList()
                        }).ToList();

                    if (metrics.Count > 1 && gauges.Count > 0)
                        _logger.Trace(() => $"Aggregated {gauges.Count} gauge(s) into {groupedGauges.Count} gauge group(s)");

                    // timings
                    var timings = metrics.Where(e => e.Type == MetricType.Timing).ToList();
                    var groupedTimings = timings
                        .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(timeBucket.Size), timeBucket.Size, e.Name))
                        .Select(e => new AggregatedTimingMetric {
                            Key = e.Key,
                            Count = e.Count(),
                            TotalDuration = e.Sum(c => (long)c.Timing),
                            MinDuration = e.Min(c => c.Timing),
                            MaxDuration = e.Max(c => c.Timing),
                            Entries = e.ToList()
                        }).ToList();

                    if (metrics.Count > 1 && timings.Count > 0)
                        _logger.Trace(() => $"Aggregated {timings.Count} timing(s) into {groupedTimings.Count} timing group(s)");

                    // store aggregated metrics

                    if (counters.Count > 0 || gauges.Count > 0 || timings.Count > 0)
                        await StoreAggregatedMetricsInternalAsync(timeBucket, groupedCounters, groupedGauges, groupedTimings).AnyContext();
                } catch (Exception ex) {
                    _logger.Error(ex, $"Error aggregating metrics: {ex.Message}");
                    throw;
                }
            }
        }

        private async Task StoreAggregatedMetricsInternalAsync(TimeBucket timeBucket, ICollection<AggregatedCounterMetric> counters, ICollection<AggregatedGaugeMetric> gauges, ICollection<AggregatedTimingMetric> timings) {
            _logger.Trace(() => $"Storing {counters.Count} counters, {gauges.Count} gauges, {timings.Count} timings.");

            try {
                await Run.WithRetriesAsync(() => StoreAggregatedMetricsAsync(timeBucket, counters, gauges, timings)).AnyContext();
            } catch (Exception ex) {
                _logger.Error(ex, $"Error storing aggregated metrics: {ex.Message}");
                throw;
            }

            await OnCountedAsync(counters.Sum(c => c.Value)).AnyContext();
            _logger.Trace("Done storing aggregated metrics");
        }

        protected abstract Task StoreAggregatedMetricsAsync(TimeBucket timeBucket, ICollection<AggregatedCounterMetric> counters, ICollection<AggregatedGaugeMetric> gauges, ICollection<AggregatedTimingMetric> timings);

        public Task<bool> WaitForCounterAsync(string statName, long count = 1, TimeSpan? timeout = null) {
            return WaitForCounterAsync(statName, () => Task.CompletedTask, count, timeout.ToCancellationToken(TimeSpan.FromSeconds(10)));
        }

        public async Task<bool> WaitForCounterAsync(string statName, Func<Task> work, long count = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (count <= 0)
                return true;

            long currentCount = count;
            var resetEvent = new AsyncAutoResetEvent(false);
            DateTime start = SystemClock.UtcNow;

            using (Counted.AddHandler((s, e) => {
                currentCount -= e.Value;
                resetEvent.Set();
                return Task.CompletedTask;
            })) {
                _logger.Trace("Wait: count={count}", currentCount);

                if (work != null)
                    await work().AnyContext();

                if (currentCount <= 0)
                    return true;

                do {
                    try {
                        await resetEvent.WaitAsync(cancellationToken).AnyContext();
                    } catch (OperationCanceledException) { }

                    _logger.Trace("Got signal: count={currentCount} expected={count}", currentCount, count);
                } while (cancellationToken.IsCancellationRequested == false && currentCount > 0);
            }

            _logger.Trace("Done waiting: count={currentCount} expected={count} success={success} time={time}", currentCount, count, currentCount <= 0, SystemClock.UtcNow.Subtract(start));

            return currentCount <= 0;
        }

        public virtual void Dispose() {
            _flushTimer?.Dispose();
            FlushAsync().GetAwaiter().GetResult();
            _queue?.Clear();
        }

        [DebuggerDisplay("Date: {EnqueuedDate} Type: {Type} Name: {Name} Counter: {Counter} Gauge: {Gauge} Timing: {Timing}")]
        protected class MetricEntry {
            public DateTime EnqueuedDate { get; } = SystemClock.UtcNow;
            public string Name { get; set; }
            public MetricType Type { get; set; }
            public int Counter { get; set; }
            public double Gauge { get; set; }
            public int Timing { get; set; }
        }

        protected enum MetricType {
            Counter,
            Gauge,
            Timing
        }

        [DebuggerDisplay("Time: {Time} Key: {Key}")]
        protected class MetricBucket {
            public string Key { get; set; }
            public DateTime Time { get; set; }
        }

        protected interface IAggregatedMetric<T> where T: class {
            MetricKey Key { get; set; }
            ICollection<MetricEntry> Entries { get; set; }
            T Add(T other);
        }

        protected class AggregatedCounterMetric : IAggregatedMetric<AggregatedCounterMetric> {
            public MetricKey Key { get; set; }
            public long Value { get; set; }
            public ICollection<MetricEntry> Entries { get; set; }

            public AggregatedCounterMetric Add(AggregatedCounterMetric other) {
                return this;
            }
        }

        protected class AggregatedGaugeMetric : IAggregatedMetric<AggregatedGaugeMetric> {
            public MetricKey Key { get; set; }
            public int Count { get; set; }
            public double Total { get; set; }
            public double Last { get; set; }
            public double Min { get; set; }
            public double Max { get; set; }
            public ICollection<MetricEntry> Entries { get; set; }

            public AggregatedGaugeMetric Add(AggregatedGaugeMetric other) {
                return this;
            }
        }

        protected class AggregatedTimingMetric : IAggregatedMetric<AggregatedTimingMetric> {
            public MetricKey Key { get; set; }
            public int Count { get; set; }
            public long TotalDuration { get; set; }
            public int MinDuration { get; set; }
            public int MaxDuration { get; set; }
            public ICollection<MetricEntry> Entries { get; set; }

            public AggregatedTimingMetric Add(AggregatedTimingMetric other) {
                return this;
            }
        }

        [DebuggerDisplay("Size: {Size} Ttl: {Ttl}")]
        protected struct TimeBucket {
            public TimeSpan Size { get; set; }
            public TimeSpan Ttl { get; set; }
        }
    }

    public class CountedEventArgs : EventArgs {
        public long Value { get; set; }
    }
}
