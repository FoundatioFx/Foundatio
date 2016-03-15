using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Foundatio.Metrics {
    [DebuggerDisplay("Time: {StartTime}-{EndTime} Count: {Count}")]
    public class TimingStat {
        public DateTime Time { get; set; }
        public int Count { get; set; }
        public long TotalDuration { get; set; }
        public int MinDuration { get; set; }
        public int MaxDuration { get; set; }
        public double AverageDuration => Count > 0 ? (double)TotalDuration / Count : 0;
    }

    [DebuggerDisplay("Time: {StartTime}-{EndTime} Count: {Count} Min: {MinDuration} Max: {MaxDuration} Total: {TotalDuration} Avg: {AverageDuration}")]
    public class TimingStatSummary {
        public TimingStatSummary(ICollection<TimingStat> stats, DateTime start, DateTime end) {
            Stats = stats;
            Count = Stats.Sum(s => s.Count);
            MinDuration = Stats.Min(s => s.MinDuration);
            MaxDuration = Stats.Max(s => s.MaxDuration);
            TotalDuration = Stats.Sum(s => s.TotalDuration);
            AverageDuration = Count > 0 ? (double)TotalDuration / Count : 0;
            StartTime = start;
            EndTime = end;
        }

        public DateTime StartTime { get; }
        public DateTime EndTime { get; }
        public ICollection<TimingStat> Stats { get; }
        public int Count { get; }
        public int MinDuration { get; }
        public int MaxDuration { get; }
        public long TotalDuration { get; }
        public double AverageDuration { get; }
    }
}