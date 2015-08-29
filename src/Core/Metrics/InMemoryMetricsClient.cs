using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : IMetricsClient {
        private readonly ConcurrentDictionary<string, CounterStats> _counters = new ConcurrentDictionary<string, CounterStats>();
        private readonly ConcurrentDictionary<string, GaugeStats> _gauges = new ConcurrentDictionary<string, GaugeStats>();
        private readonly ConcurrentDictionary<string, TimingStats> _timings = new ConcurrentDictionary<string, TimingStats>();
        private readonly ConcurrentDictionary<string, AsyncAutoResetEvent> _counterEvents = new ConcurrentDictionary<string, AsyncAutoResetEvent>();
        private Timer _statsDisplayTimer;

        public void StartDisplayingStats(TimeSpan? interval = null, TextWriter writer = null) {
            if (interval == null)
                interval = TimeSpan.FromSeconds(10);

            _statsDisplayTimer = new Timer(OnDisplayStats, writer, interval.Value, interval.Value);
        }

        public void StopDisplayingStats() {
            if (_statsDisplayTimer == null)
                return;
            
            _statsDisplayTimer.Change(Timeout.Infinite, Timeout.Infinite);
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

        public IDictionary<string, CounterStats> Counters { get { return _counters; } }
        public IDictionary<string, TimingStats> Timings { get { return _timings; } }
        public IDictionary<string, GaugeStats> Gauges { get { return _gauges; } }

        public Task CounterAsync(string statName, int value = 1) {
            _counters.AddOrUpdate(statName, key => new CounterStats(value), (key, stats) => {
                stats.Increment(value);
                return stats;
            });
            AsyncAutoResetEvent waitHandle;
            _counterEvents.TryGetValue(statName, out waitHandle);
            waitHandle?.Set();

            return TaskHelper.Completed();
        }
        
        public async Task<bool> WaitForCounterAsync(string statName, TimeSpan timeout, long count = 1, Func<Task> work = null) {
            try {
                return await WaitForCounterAsync(statName, count, new CancellationTokenSource(timeout).Token, work).AnyContext();
            } catch (TaskCanceledException) {
                return false;
            }
        }

        public async Task<bool> WaitForCounterAsync(string statName, long count = 1, CancellationToken cancellationToken = default(CancellationToken), Func<Task> work = null) {
            if (count == 0)
                return true;

            long startingCount = GetCount(statName);
            long expectedCount = startingCount + count;
            if (work != null)
                await work().AnyContext();

            if (GetCount(statName) >= expectedCount)
                return true;

            var waitHandle = _counterEvents.GetOrAdd(statName, s => new AsyncAutoResetEvent(false));
            do {
                try {
                    await waitHandle.WaitAsync(cancellationToken).AnyContext();
                } catch (TaskCanceledException) {
                    return false;
                }
            } while (GetCount(statName) < expectedCount);

            return true;
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

        public long Value { get { return _value; } }
        public long CurrentValue { get { return _currentValue; } }
        public double Rate { get { return ((double)Value / _stopwatch.Elapsed.TotalSeconds); } }
        public double CurrentRate { get { return ((double)CurrentValue / _currentStopwatch.Elapsed.TotalSeconds); } }

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

        public int Count { get { return _count; } }
        public long Total { get { return _total; } }
        public long Current { get { return _current; } }
        public long Min { get { return _min; } }
        public long Max { get { return _max; } }
        public double Average { get { return _average; } }

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

        public int Count { get { return _count; } }
        public double Total { get { return _total; } }
        public double Current { get { return _current; } }
        public double Max { get { return _max; } }
        public double Average { get { return _average; } }

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