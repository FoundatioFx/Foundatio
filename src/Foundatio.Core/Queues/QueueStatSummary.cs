using System;
using Foundatio.Metrics;

namespace Foundatio.Queues {
    public class QueueStatSummary {
        public GaugeStatSummary Count { get; set; }
        public GaugeStatSummary Working { get; set; }
        public GaugeStatSummary Deadletter { get; set; }
        public CounterStatSummary Enqueued { get; set; }
        public TimingStatSummary QueueTime { get; set; }
        public CounterStatSummary Dequeued { get; set; }
        public CounterStatSummary Completed { get; set; }
        public CounterStatSummary Abandoned { get; set; }
        public TimingStatSummary ProcessTime { get; set; }
    }
}
