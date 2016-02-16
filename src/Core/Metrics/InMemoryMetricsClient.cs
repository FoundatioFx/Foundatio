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
                    CounterStats counter;
                    if (_counters.TryGetValue(key, out counter))
                        writer.WriteLine("Counter: {0} Value: {1} Rate: {2} Rate: {3}", key.PadRight(maxNameLength), counter.Value.ToString().PadRight(12), counter.RecentRate.ToString("#,##0.##'/s'").PadRight(12), counter.Rate.ToString("#,##0.##'/s'"));
                }

                foreach (var key in _gauges.Keys.ToList()) {
                    GaugeStats gauge;
                    if (_gauges.TryGetValue(key, out gauge))
                        writer.WriteLine("  Gauge: {0} Value: {1}  Avg: {2} Max: {3} Count: {4}", key.PadRight(maxNameLength), gauge.Current.ToString("#,##0.##").PadRight(12), gauge.Average.ToString("#,##0.##").PadRight(12), gauge.Max.ToString("#,##0.##"), gauge.Count);
                }

                foreach (var key in _timings.Keys.ToList()) {
                    TimingStats timing;
                    if (_timings.TryGetValue(key, out timing))
                        writer.WriteLine(" Timing: {0}   Min: {1}  Avg: {2} Max: {3} Count: {4}", key.PadRight(maxNameLength), timing.Min.ToString("#,##0.##'ms'").PadRight(12), timing.Average.ToString("#,##0.##'ms'").PadRight(12), timing.Max.ToString("#,##0.##'ms'"), timing.Count);
                }

                if (_counters.Count > 0 || _gauges.Count > 0 || _timings.Count > 0)
                    writer.WriteLine("-----");
            }

            _isDisplayingStats = false;
        }

        public MetricStats GetMetricStats() {
            return new MetricStats {
                Counters = _counters.ToDictionary(kvp => kvp.Key, kvp => (ICounterStats)kvp.Value),
                Timings = _timings.ToDictionary(kvp => kvp.Key, kvp => (ITimingStats)kvp.Value),
                Gauges = _gauges.ToDictionary(kvp => kvp.Key, kvp => (IGaugeStats)kvp.Value)
            };
        }

        public IDictionary<string, CounterStats> Counters => _counters;
        public IDictionary<string, TimingStats> Timings => _timings;
        public IDictionary<string, GaugeStats> Gauges => _gauges;

        public Task CounterAsync(string statName, int value = 1) {
#if DEBUG
            Logger.Trace().Message($"Counter: {statName} value: {value}").Write();
#endif
            _counters.AddOrUpdate(statName, key => new CounterStats(value), (key, stats) => {
                stats.Increment(value);
                return stats;
            });
            AsyncManualResetEvent waitHandle;
            _counterEvents.TryGetValue(statName, out waitHandle);
            waitHandle?.Set();

            return TaskHelper.Completed();
        }
        
        public Task<bool> WaitForCounterAsync(string statName, long count = 1, TimeSpan? timeout = null) {
            return WaitForCounterAsync(statName, TaskHelper.Completed, count, timeout.ToCancellationToken(TimeSpan.FromSeconds(10)));
        }
        
        public async Task<bool> WaitForCounterAsync(string statName, Func<Task> work, long count = 1, CancellationToken cancellationToken = default(CancellationToken)) {
            if (count <= 0)
                return true;
            
            long startingCount = GetCount(statName);
            long expectedCount = startingCount + count;
#if DEBUG
            Logger.Trace().Message($"Wait: count={count} current={startingCount}").Write();
#endif
            if (work != null)
                await work().AnyContext();

            if (GetCount(statName) >= expectedCount)
                return true;
            
            // TODO: Should we update this to use monitors?
            var resetEvent = _counterEvents.GetOrAdd(statName, s => new AsyncManualResetEvent(false));
            do {
                try {
                    await resetEvent.WaitAsync(cancellationToken).AnyContext();
                } catch (OperationCanceledException) {}
#if DEBUG
                Logger.Trace().Message($"Got signal: count={GetCount(statName)} expected={expectedCount}").Write();
#endif
                resetEvent.Reset();
            } while (cancellationToken.IsCancellationRequested == false && GetCount(statName) < expectedCount);
#if DEBUG
            Logger.Trace().Message($"Done waiting: count={GetCount(statName)} expected={expectedCount} success={!cancellationToken.IsCancellationRequested}").Write();
#endif
            return !cancellationToken.IsCancellationRequested;
        }
        public Task GaugeAsync(string statName, double value) {
            _gauges.AddOrUpdate(statName, key => new GaugeStats(value), (key, stats) => {
                stats.Set(value);
                return stats;
            });

            return TaskHelper.Completed();
        }

        public Task TimerAsync(string statName, int milliseconds) {
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

    public class CounterStats : ICounterStats {
        public CounterStats(long value) {
            Increment(value);
        }

        private long _value;
        private long _recentValue;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly Stopwatch _recentStopwatch = new Stopwatch();

        public long Value => _value;
        public long RecentValue => _recentValue;
        public double Rate => ((double)Value / _stopwatch.Elapsed.TotalSeconds);
        public double RecentRate => ((double)RecentValue / _recentStopwatch.Elapsed.TotalSeconds);

        private static readonly object _lock = new object();
        public void Increment(long value) {
            lock (_lock) {
                _value += value;
                _recentValue += value;

                if (!_stopwatch.IsRunning)
                    _stopwatch.Start();

                if (!_recentStopwatch.IsRunning)
                    _recentStopwatch.Start();

                if (_recentStopwatch.Elapsed > TimeSpan.FromMinutes(1)) {
                    _recentValue = 0;
                    _recentStopwatch.Restart();
                }
            }
        }
    }

    public class TimingStats : ITimingStats {
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

    public class GaugeStats : IGaugeStats {
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