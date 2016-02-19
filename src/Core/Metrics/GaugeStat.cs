using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Metrics {
    public class GaugeStat {
        public DateTime Time { get; set; }
        public double Max { get; set; }
        public double Last { get; set; }
    }

    public class GaugeStatSummary {
        public GaugeStatSummary(ICollection<GaugeStat> stats, DateTime start, DateTime end) {
            Stats = stats;
            Last = Stats.Last().Last;
            Max = Stats.Max(s => s.Max);
            StartTime = start;
            EndTime = end;
        }

        public DateTime StartTime { get; }
        public DateTime EndTime { get; }
        public ICollection<GaugeStat> Stats { get; }
        public double Last { get; }
        public double Max { get; }
    }
}