using System.Collections.Generic;

namespace Foundatio.Metrics {
    public interface IHaveMetricStats {
        MetricStats GetMetricStats();
    }

    public class MetricStats {
        public IDictionary<string, ICounterStats> Counters { get; set; }
        public IDictionary<string, ITimingStats> Timings { get; set; }
        public IDictionary<string, IGaugeStats> Gauges { get; set; }
    }

    public interface ICounterStats {
        long Value { get; }
        long RecentValue { get; }
        double Rate { get; }
        double RecentRate { get; }
    }

    public interface ITimingStats {
        int Count { get; }
        long Total { get; }
        long Current { get; }
        long Min { get; }
        long Max { get; }
        double Average { get; }
    }

    public interface IGaugeStats {
        int Count { get; }
        double Total { get; }
        double Current { get; }
        double Max { get; }
        double Average { get; }
    }
}
