using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Extensions;

namespace Foundatio.Metrics {
    public class CounterStat {
        public DateTime Time { get; set; }
        public long Count { get; set; }
    }

    public class CounterStatSummary {
        public CounterStatSummary(ICollection<CounterStat> stats, DateTime start, DateTime end) {
            Stats.AddRange(stats);
            Count = Stats.Sum(s => s.Count);
            StartTime = start;
            EndTime = end;
        }

        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
        public ICollection<CounterStat> Stats { get; } = new List<CounterStat>();
        public long Count { get; private set; }
    }
}