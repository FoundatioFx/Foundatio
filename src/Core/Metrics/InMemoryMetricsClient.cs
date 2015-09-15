using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : IMetricsClient {
        private readonly ConcurrentDictionary<string, CounterStats> _counters = new ConcurrentDictionary<string, CounterStats>();
        private readonly ConcurrentDictionary<string, GaugeStats> _gauges = new ConcurrentDictionary<string, GaugeStats>();
        private readonly ConcurrentDictionary<string, TimingStats> _timings = new ConcurrentDictionary<string, TimingStats>();
        private readonly ConcurrentDictionary<string, AsyncManualResetEvent> _counterEvents = new ConcurrentDictionary<string, AsyncManualResetEvent>();
        private Timer _statsDisplayTimer;

        public void StartDisplayingStats(TimeSpan? interval = null, TextWriter writer = null) {
            if (interval == null)
                interval = TimeSpan.FromSeconds(10);

            _statsDisplayTimer = new Timer(OnDisplayStats, writer, interval.Value, interval.Value);
        }

        public void StopDisplayingStats() {
            _statsDisplayTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void Reset() {
            _counters.Clear();
            _gauges.Clear();
            _timings.Clear();
        }

        private void OnDisplayStats(object state) {
            DisplayStats(state as TextWriter);
        }

        private static bool _isDisplayingStats = false;
        private static readonly object _statsDisplayLock = new object();
        public void DisplayStats(TextWriter writer = null) {
            if (writer == null)
                writer = new TraceTextWriter();

            if (_isDisplayingStats)
                return;

            lock (_statsDisplayLock) {
                _isDisplayingStats = true;

                int maxNameLength = 1;
                if (_counters.Count > 0)
                    maxNameLength = Math.Max(_counters.Max(c => c.Key.Length), maxNameLength);
                if (_gauges.Count > 0)
                    maxNameLength = Math.Max(_gauges.Max(c => c.Key.Length), maxNameLength);
                if (_timings.Count > 0)
                    maxNameLength = Math.Max(_timings.Max(c => c.Key.Length), maxNameLength);

                foreach (var key in _counters.Keys.ToList()) {
                    var counter = _counters[key];
                    writer.WriteLine("Counter: {0} Value: {1} Rate: {2} Rate: {3}", key.PadRight(maxNameLength), counter.Value.ToString().PadRight(12), counter.CurrentRate.ToString("#,##0.##'/s'").PadRight(12), counter.Rate.ToString("#,##0.##'/s'"));
                }

                foreach (var key in _gauges.Keys.ToList()) {
                    var gauge = _gauges[key];
                    writer.WriteLine("  Gauge: {0} Value: {1}  Avg: {2} Max: {3}", key.PadRight(maxNameLength), gauge.Current.ToString("#,##0.##").PadRight(12), gauge.Average.ToString("#,##0.##").PadRight(12), gauge.Max.ToString("#,##0.##"));
                }

                foreach (var key in _timings.Keys.ToList()) {
                    var timing = _timings[key];
                    writer.WriteLine(" Timing: {0}   Min: {1}  Avg: {2} Max: {3}", key.PadRight(maxNameLength), timing.Min.ToString("#,##0.##'ms'").PadRight(12), timing.Average.ToString("#,##0.##'ms'").PadRight(12), timing.Max.ToString("#,##0.##'ms'"));
                }

                if (_counters.Count > 0 || _gauges.Count > 0 || _timings.Count > 0)
                    writer.WriteLine("-----");
            }

            _isDisplayingStats = false;
        }

        public MetricStats GetMetricStats() {
            return new MetricStats {
                Counters = _counters,
                Timings = _timings,
                Gauges = _gauges
            };
        }

        public IDictionary<string, CounterStats> Counters => _counters;
        public IDictionary<string, TimingStats> Timings => _timings;
        public IDictionary<string, GaugeStats> Gauges => _gauges;

        public Task CounterAsync(string statName, int value = 1) {
            Logger.Trace().Message("Counter: {0} value: {1}", statName, value).Write();
            _counters.AddOrUpdate(statName, key => new CounterStats(value), (key, stats) => {
                stats.Increment(value);
                return stats;
            });
            AsyncManualResetEvent waitHandle;
            _counterEvents.TryGetValue(statName, out waitHandle);
            waitHandle?.Set();

            return TaskHelper.Completed();
        }
        
        public Task<bool> WaitForCounterAsync(string statName, TimeSpan timeout, long count = 1) {
            return WaitForCounterAsync(statName, TaskHelper.Completed, timeout, count);
        }

        public async Task<bool> WaitForCounterAsync(string statName, Func<Task> work, TimeSpan? timeout = null, long count = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (count <= 0)
                return true;

            if (!timeout.HasValue)
                timeout = TimeSpan.FromSeconds(10);

            if (cancellationToken == CancellationToken.None)
                cancellationToken = new CancellationTokenSource(timeout.Value).Token;

            long startingCount = GetCount(statName);
            long expectedCount = startingCount + count;
            Logger.Trace().Message("Wait: count={0} current={1}", count, startingCount).Write();
            if (work != null)
                await work().AnyContext();

            if (GetCount(statName) >= expectedCount)
                return true;
            
            var resetEvent = _counterEvents.GetOrAdd(statName, s => new AsyncManualResetEvent(false));
            do {
                try {
                    await resetEvent.WaitAsync(cancellationToken).AnyContext();
                } catch (OperationCanceledException) {}
                Logger.Trace().Message("Got signal: count={0} expected={1}", GetCount(statName), expectedCount).Write();
                resetEvent.Reset();
            } while (cancellationToken.IsCancellationRequested == false && GetCount(statName) < expectedCount);

            Logger.Trace().Message("Done waiting: count={0} expected={1} success={2}", GetCount(statName), expectedCount, !cancellationToken.IsCancellationRequested).Write();
            return !cancellationToken.IsCancellationRequested;
        }
        public Task GaugeAsync(string statName, double value) {
            _gauges.AddOrUpdate(statName, key => new GaugeStats(value), (key, stats) => {
                stats.Set(value);
                return stats;
            });

            return TaskHelper.Completed();
        }

        public Task TimerAsync(string statName, long milliseconds) {
            _timings.AddOrUpdate(statName, key => new TimingStats(milliseconds), (key, stats) => {
                stats.Set(milliseconds);
                return stats;
            });

            return TaskHelper.Completed();
        }

        public long GetCount(string statName) {
            return _counters.ContainsKey(statName) ? _counters[statName].Value : 0;
        }

        public double GetGaugeValue(string statName) {
            return _gauges.ContainsKey(statName) ? _gauges[statName].Current : 0d;
        }

        public void Dispose() {}
    }

    public class MetricStats {
        public IDictionary<string, CounterStats> Counters { get; internal set; }
        public IDictionary<string, TimingStats> Timings { get; internal set; }
        public IDictionary<string, GaugeStats> Gauges { get; internal set; }
    }

    public class CounterStats {
        public CounterStats(long value) {
            Increment(value);
        }

        private long _value;
        private long _currentValue;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly Stopwatch _currentStopwatch = new Stopwatch();

        public long Value => _value;
        public long CurrentValue => _currentValue;
        public double Rate => ((double)Value / _stopwatch.Elapsed.TotalSeconds);
        public double CurrentRate => ((double)CurrentValue / _currentStopwatch.Elapsed.TotalSeconds);

        private static readonly object _lock = new object();
        public void Increment(long value) {
            lock (_lock) {
                _value += value;
                _currentValue += value;

                if (!_stopwatch.IsRunning)
                    _stopwatch.Start();

                if (!_currentStopwatch.IsRunning)
                    _currentStopwatch.Start();

                if (_currentStopwatch.Elapsed > TimeSpan.FromMinutes(1)) {
                    _currentValue = 0;
                    _currentStopwatch.Reset();
                    _currentStopwatch.Start();
                }
            }
        }
    }

    public class TimingStats {
        public TimingStats(long value) {
            Set(value);
        }

        private long _current = 0;
        private long _max = 0;
        private long _min = 0;
        private int _count = 0;
        private long _total = 0;
        private double _average = 0d;

        public int Count => _count;
        public long Total => _total;
        public long Current => _current;
        public long Min => _min;
        public long Max => _max;
        public double Average => _average;

        private static readonly object _lock = new object();
        public void Set(long value) {
            lock (_lock) {
                _current = value;
                _count++;
                _total += value;
                _average = (double)_total / _count;

                if (value < _min || _min == 0)
                    _min = value;

                if (value > _max)
                    _max = value;
            }
        }
    }

    public class GaugeStats {
        public GaugeStats(double value) {
            Set(value);
        }

        private double _current = 0d;
        private double _max = 0d;
        private int _count = 0;
        private double _total = 0d;
        private double _average = 0d;

        public int Count => _count;
        public double Total => _total;
        public double Current => _current;
        public double Max => _max;
        public double Average => _average;

        private static readonly object _lock = new object();
        public void Set(double value) {
            lock (_lock) {
                _current = value;
                _count++;
                _total += value;
                _average = _total / _count;

                if (value > _max)
                    _max = value;
            }
        }
    }
}