using Foundatio.Metrics;
using Xunit;

namespace Foundatio.Tests.Metrics {
    public class InMemoryMetricsTests {
        [Fact]
        public void CanIncrementCounter() {
            var metrics = new InMemoryMetricsClient();
            metrics.DisplayStats();

            metrics.Counter("c1");
            Assert.Equal(1, metrics.GetCount("c1"));

            metrics.Counter("c1", 5);
            Assert.Equal(6, metrics.GetCount("c1"));

            var counter = metrics.Counters["c1"];
            Assert.InRange(counter.Rate, 500, 2000);

            metrics.Gauge("g1", 2.534);
            Assert.Equal(2.534, metrics.GetGaugeValue("g1"));

            metrics.Timer("t1", 50788);

            metrics.DisplayStats();
            metrics.DisplayStats();

            var stats = metrics.GetMetricStats();
        }
    }
}