using System;
using Foundatio.Xunit;
using Foundatio.Metrics;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using System.Linq;

namespace Foundatio.Tests.Metrics {
    public class DiagnosticsMetricsTests : TestWithLoggingBase, IDisposable {
        private readonly DiagnosticsMetricsClient _client;

        public DiagnosticsMetricsTests(ITestOutputHelper output) : base(output) {
            Log.MinimumLevel = LogLevel.Trace;
            _client = new DiagnosticsMetricsClient("Test");
        }

        [Fact]
        public void Counter() {
            using var metricsCollector = new DiagnosticsMetricsCollector("Test");

            _client.Counter("counter");

            Assert.Single(metricsCollector.IntMeasurements);
            Assert.Equal("counter", metricsCollector.IntMeasurements.Single().Name);
            Assert.Equal(1, metricsCollector.IntMeasurements.Single().Value);
        }

        [Fact]
        public void CounterWithValue() {
            using var metricsCollector = new DiagnosticsMetricsCollector("Test");

            _client.Counter("counter", 5);
            _client.Counter("counter", 3);

            Assert.Equal(2, metricsCollector.IntMeasurements.Count);
            Assert.All(metricsCollector.IntMeasurements, m => {
                Assert.Equal("counter", m.Name);
            });
            Assert.Equal(8, metricsCollector.GetIntSum("counter"));
            Assert.Equal(2, metricsCollector.GetIntCount("counter"));
        }

        [Fact]
        public void Gauge() {
            using var metricsCollector = new DiagnosticsMetricsCollector("Test");

            _client.Gauge("gauge", 1.1);

            metricsCollector.RecordObservableInstruments();

            Assert.Single(metricsCollector.DoubleMeasurements);
            Assert.Equal("gauge", metricsCollector.DoubleMeasurements.Single().Name);
            Assert.Equal(1.1, metricsCollector.DoubleMeasurements.Single().Value);
        }

        [Fact]
        public void Timer() {
            using var metricsCollector = new DiagnosticsMetricsCollector("Test");

            _client.Timer("timer", 450);
            _client.Timer("timer", 220);

            Assert.Equal(670, metricsCollector.GetIntSum("timer"));
            Assert.Equal(2, metricsCollector.GetIntCount("timer"));
        }

        public void Dispose() {
            _client.Dispose();
        }
    }
}