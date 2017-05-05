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
        public GaugeStatSummary(string name, ICollection<GaugeStat> stats, DateTime start, DateTime end) {
            Name = name;
            Stats = stats;
            Count = stats.Count > 0 ? Stats.Sum(s => s.Count) : 0;
            Total = stats.Count > 0 ? Stats.Sum(s => s.Total) : 0;
            Last = Stats.LastOrDefault()?.Last ?? 0;
            Min = stats.Count > 0 ? Stats.Min(s => s.Min) : 0;
            Max = stats.Count > 0 ? Stats.Max(s => s.Max) : 0;
            StartTime = start;
            EndTime = end;
            Average = Count > 0 ? Total / Count : 0;
        }

        public string Name { get; }
        public DateTime StartTime { get; }
        public DateTime EndTime { get; }
        public ICollection<GaugeStat> Stats { get; }
        public int Count { get; set; }
        public double Total { get; set; }
        public double Last { get; }
        public double Min { get; set; }
        public double Max { get; }
        public double Average { get; }

        public override string ToString() {
            return $"Counter: {Name} Time: {StartTime}-{EndTime} Max: {Max} Last: {Last}";
        }
    }
}