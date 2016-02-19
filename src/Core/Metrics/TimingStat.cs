using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Metrics {
    public class TimingStat {
        public DateTime Time { get; set; }
        public int Count { get; set; }
        public long TotalDuration { get; set; }
        public int MinDuration { get; set; }
        public int MaxDuration { get; set; }
        public double AverageDuration => (double)TotalDuration / Count;
    }

    public class TimingStatSummary {
        public TimingStatSummary(ICollection<TimingStat> stats, DateTime start, DateTime end) {
            Stats = stats;
            Count = Stats.Sum(s => s.Count);
            MinDuration = Stats.Min(s => s.MinDuration);
            MaxDuration = Stats.Max(s => s.MaxDuration);
            TotalDuration = Stats.Sum(s => s.TotalDuration);
            AverageDuration = (double)TotalDuration / Count;
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