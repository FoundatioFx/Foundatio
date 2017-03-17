using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Metrics {
    public abstract class BufferedMetricsClientBase : IBufferedMetricsClient {
        private readonly ConcurrentQueue<MetricEntry> _queue = new ConcurrentQueue<MetricEntry>();
        private readonly TimeSpan _groupSize = TimeSpan.FromMinutes(1);

        private readonly Timer _flushTimer;
        private readonly bool _buffered;
        protected readonly ILogger _logger;

        public BufferedMetricsClientBase(bool buffered = true, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger(GetType());
            _buffered = buffered;

            if (buffered)
                _flushTimer = new Timer(OnMetricsTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        public AsyncEvent<CountedEventArgs> Counted { get; } = new AsyncEvent<CountedEventArgs>(true);

        protected virtual Task OnCountedAsync(int value) {
            var counted = Counted;
            if (counted == null)
                return Task.CompletedTask;

            var args = new CountedEventArgs { Value = value };
            return counted.InvokeAsync(this, args);
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

        protected virtual async Task SubmitMetricsAsync(List<MetricEntry> metrics) {
            // counters
            var counters = metrics.Where(e => e.Type == MetricType.Counter)
                .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(_groupSize), _groupSize, e.Name))
                .Select(e => new AggregatedCounterMetric { Key = e.Key, Value = e.Sum(c => c.Counter), Entries = e.ToList() }).ToList();

            if (metrics.Count > 1 && counters.Count > 0)
                _logger.Trace(() => $"Aggregated {counters.Count} counters");

            // gauges
            var gauges = metrics.Where(e => e.Type == MetricType.Gauge)
                .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(_groupSize), _groupSize, e.Name))
                .Select(e => new AggregatedGaugeMetric { Key = e.Key, Count = e.Count(), Total = e.Sum(c => c.Gauge), Last = e.Last().Gauge, Min = e.Min(c => c.Gauge), Max = e.Max(c => c.Gauge), Entries = e.ToList() }).ToList();

            if (metrics.Count > 1 && gauges.Count > 0)
                _logger.Trace(() => $"Aggregated {gauges.Count} gauges");

            // timings
            var timings = metrics.Where(e => e.Type == MetricType.Timing)
                .GroupBy(e => new MetricKey(e.EnqueuedDate.Floor(_groupSize), _groupSize, e.Name))
                .Select(e => new AggregatedTimingMetric { Key = e.Key, Count = e.Count(), TotalDuration = e.Sum(c => c.Timing), MinDuration = e.Min(c => c.Timing), MaxDuration = e.Max(c => c.Timing), Entries = e.ToList() }).ToList();

            if (metrics.Count > 1 && timings.Count > 0)
                _logger.Trace(() => $"Aggregated {timings.Count} timings");

            // store aggregated metrics

            if (counters.Count > 0 || gauges.Count > 0 || timings.Count > 0)
                await Run.WithRetriesAsync(() => StoreAggregatedMetricsAsync(counters, gauges, timings)).AnyContext();
        }

        private async Task StoreAggregatedMetricsInternalAsync(ICollection<AggregatedCounterMetric> counters, ICollection<AggregatedGaugeMetric> gauges, ICollection<AggregatedTimingMetric> timings) {
            _logger.Trace(() => $"Storing {counters.Count} counters, {gauges.Count} gauges, {timings.Count} timings.");
            
            await StoreAggregatedMetricsAsync(counters, gauges, timings).AnyContext();

            await OnCountedAsync(counters.Sum(c => c.Value)).AnyContext();

            _logger.Trace("Done storing aggregated metrics");
        }

        protected abstract Task StoreAggregatedMetricsAsync(ICollection<AggregatedCounterMetric> counters, ICollection<AggregatedGaugeMetric> gauges, ICollection<AggregatedTimingMetric> timings);

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

        protected class AggregatedCounterMetric {
            public MetricKey Key { get; set; }
            public int Value { get; set; }
            public ICollection<MetricEntry> Entries { get; set; }
        }

        protected class AggregatedGaugeMetric {
            public MetricKey Key { get; set; }
            public int Count { get; set; }
            public double Total { get; set; }
            public double Last { get; set; }
            public double Min { get; set; }
            public double Max { get; set; }
            public ICollection<MetricEntry> Entries { get; set; }
        }

        protected class AggregatedTimingMetric {
            public MetricKey Key { get; set; }
            public int Count { get; set; }
            public int TotalDuration { get; set; }
            public int MinDuration { get; set; }
            public int MaxDuration { get; set; }
            public ICollection<MetricEntry> Entries { get; set; }
        }
    }

    public class CountedEventArgs : EventArgs {
        public int Value { get; set; }
    }
}
