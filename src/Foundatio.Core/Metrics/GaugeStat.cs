using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Foundatio.Metrics {
    [DebuggerDisplay("Time: {Time} Max: {Max} Last: {Last}")]
    public class GaugeStat {
        public DateTime Time { get; set; }
        public int Count { get; set; }
        public double Total { get; set; }
        public double Last { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Average => Count > 0 ? Total / Count : 0;
    }

    [DebuggerDisplay("Time: {StartTime}-{EndTime} Max: {Max} Last: {Last}")]
    public class GaugeStatSummary {
        public GaugeStatSummary(ICollection<GaugeStat> stats, DateTime start, DateTime end) {
            Stats = stats;
            Count = Stats.Sum(s => s.Count);
            Total = Stats.Sum(s => s.Total);
            Last = Stats.Last().Last;
            Min = Stats.Min(s => s.Min);
            Max = Stats.Max(s => s.Max);
            StartTime = start;
            EndTime = end;
            Average = Count > 0 ? Total / Count : 0;
        }

        public DateTime StartTime { get; }
        public DateTime EndTime { get; }
        public ICollection<GaugeStat> Stats { get; }
        public int Count { get; set; }
        public double Total { get; set; }
        public double Last { get; }
        public double Min { get; set; }
        public double Max { get; }
        public double Average { get; }
    }
}