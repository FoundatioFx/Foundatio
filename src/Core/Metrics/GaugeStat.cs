using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Foundatio.Metrics {
    [DebuggerDisplay("Time: {Time} Max: {Max} Last: {Last}")]
    public class GaugeStat {
        public DateTime Time { get; set; }
        public double Max { get; set; }
        public double Last { get; set; }
    }

    [DebuggerDisplay("Time: {StartTime}-{EndTime} Max: {Max} Last: {Last}")]
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