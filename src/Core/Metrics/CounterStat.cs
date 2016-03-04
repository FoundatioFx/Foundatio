using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Foundatio.Extensions;

namespace Foundatio.Metrics {
    [DebuggerDisplay("Time: {Time} Count: {Count}")]
    public class CounterStat {
        public DateTime Time { get; set; }
        public long Count { get; set; }
    }

    [DebuggerDisplay("Time: {StartTime}-{EndTime} Count: {Count}")]
    public class CounterStatSummary {
        public CounterStatSummary(string name, ICollection<CounterStat> stats, DateTime start, DateTime end) {
            Name = name;
            Stats.AddRange(stats);
            Count = Stats.Sum(s => s.Count);
            StartTime = start;
            EndTime = end;
        }

        public string Name { get; private set; }
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
        public ICollection<CounterStat> Stats { get; } = new List<CounterStat>();
        public long Count { get; private set; }

        public override string ToString() {
            return $"Counter: {Name} Value: {Count}";
        }
    }
}