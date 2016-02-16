using System;
using System.Threading.Tasks;

namespace Foundatio.Metrics {
    public interface IMetricsClientStats {
        Task<CounterStatSummary> GetCounterStatsAsync(string statName, DateTime start, DateTime end);
        Task<GaugeStatSummary> GetGaugeStatsAsync(string statName, DateTime start, DateTime end);
        Task<TimingStatSummary> GetTimerStatsAsync(string statName, DateTime start, DateTime end);
    }
}