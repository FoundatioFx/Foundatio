using System;
using System.IO;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public interface IMetricsClientStats {
        Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? start = null, DateTime? end = null);
        Task<GaugeStatSummary> GetGaugeStatsAsync(string name, DateTime? start = null, DateTime? end = null);
        Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? start = null, DateTime? end = null);
    }

    public static class MetricsClientStatsExtensions {
        public static async Task<long> GetCounterCountAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null) {
            var result = await stats.GetCounterStatsAsync(name, start, end).AnyContext();
            return result.Count;
        }

        public static async Task<double> GetLastGaugeValueAsync(this IMetricsClientStats stats, string name, DateTime? start = null, DateTime? end = null) {
            var result = await stats.GetGaugeStatsAsync(name, start, end).AnyContext();
            return result.Last;
        }

        public static async Task DisplayCounterAsync(this IMetricsClientStats stats, string name, TextWriter writer = null, DateTime? start = null, DateTime? end = null) {
            if (writer == null)
                writer = new TraceTextWriter();

            var counter = await stats.GetCounterStatsAsync(name, start, end).AnyContext();
            writer.WriteLine("Counter: {0} Value: {1}", name, counter.Count);

            //if (_isDisplayingStats)
            //    return;

            //lock (_statsDisplayLock) {
            //    _isDisplayingStats = true;

            //    int maxNameLength = 1;
            //    if (_counters.Count > 0)
            //        maxNameLength = Math.Max(_counters.Max(c => c.Key.Length), maxNameLength);
            //    if (_gauges.Count > 0)
            //        maxNameLength = Math.Max(_gauges.Max(c => c.Key.Length), maxNameLength);
            //    if (_timings.Count > 0)
            //        maxNameLength = Math.Max(_timings.Max(c => c.Key.Length), maxNameLength);

            //    foreach (var key in _counters.Keys.ToList()) {
            //        CounterStats counter;
            //        if (_counters.TryGetValue(key, out counter))
            //            writer.WriteLine("Counter: {0} Value: {1} Rate: {2} Rate: {3}", key.PadRight(maxNameLength), counter.Value.ToString().PadRight(12), counter.RecentRate.ToString("#,##0.##'/s'").PadRight(12), counter.Rate.ToString("#,##0.##'/s'"));
            //    }

            //    foreach (var key in _gauges.Keys.ToList()) {
            //        GaugeStats gauge;
            //        if (_gauges.TryGetValue(key, out gauge))
            //            writer.WriteLine("  Gauge: {0} Value: {1}  Avg: {2} Max: {3} Count: {4}", key.PadRight(maxNameLength), gauge.Current.ToString("#,##0.##").PadRight(12), gauge.Average.ToString("#,##0.##").PadRight(12), gauge.Max.ToString("#,##0.##"), gauge.Count);
            //    }

            //    foreach (var key in _timings.Keys.ToList()) {
            //        TimingStats timing;
            //        if (_timings.TryGetValue(key, out timing))
            //            writer.WriteLine(" Timing: {0}   Min: {1}  Avg: {2} Max: {3} Count: {4}", key.PadRight(maxNameLength), timing.Min.ToString("#,##0.##'ms'").PadRight(12), timing.Average.ToString("#,##0.##'ms'").PadRight(12), timing.Max.ToString("#,##0.##'ms'"), timing.Count);
            //    }

            //    if (_counters.Count > 0 || _gauges.Count > 0 || _timings.Count > 0)
            //        writer.WriteLine("-----");
            //}

            //_isDisplayingStats = false;
        }
    }
}