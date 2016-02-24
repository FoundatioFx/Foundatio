using System;
using System.Threading.Tasks;
using Foundatio.Extensions;

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
    }
}