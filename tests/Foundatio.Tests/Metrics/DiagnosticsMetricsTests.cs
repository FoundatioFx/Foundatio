using System;
using Foundatio.Xunit;
using Foundatio.Metrics;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Foundatio.Tests.Metrics {
    public class DiagnosticsMetricsTests : TestWithLoggingBase, IDisposable {
        private readonly DiagnosticsMetricsClient _client;

        public DiagnosticsMetricsTests(ITestOutputHelper output) : base(output) {
            Log.MinimumLevel = LogLevel.Trace;
            _client = new DiagnosticsMetricsClient("Test");
        }

        [Fact]
        public void Counter() {
            using var meterListener = new MeterListener();
            meterListener.InstrumentPublished = (instrument, listener) => {
                if (instrument.Meter.Name == "Test")
                    listener.EnableMeasurementEvents(instrument);
            };
            int count = 0;
            int measurements = 0;

            meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) => {
                if (instrument.Name == "counter") {
                    count += measurement;
                    measurements++;
                }
            });
            meterListener.Start();

            _client.Counter("counter");

            Thread.Sleep(100);

            Assert.Equal(1, count);
            Assert.Equal(1, measurements);
        }

        [Fact]
        public void CounterWithValue() {
            using var meterListener = new MeterListener();
            meterListener.InstrumentPublished = (instrument, listener) => {
                if (instrument.Meter.Name == "Test")
                    listener.EnableMeasurementEvents(instrument);
            };

            int count = 0;
            int measurements = 0;

            meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) => {
                if (instrument.Name == "counter") {
                    count += measurement;
                    measurements++;
                }
            });
            meterListener.Start();

            _client.Counter("counter", 5);
            _client.Counter("counter", 3);

            Thread.Sleep(100);

            Assert.Equal(8, count);
            Assert.Equal(2, measurements);
        }

        [Fact]
        public void Gauge() {
            using var meterListener = new MeterListener();
            meterListener.InstrumentPublished = (instrument, listener) => {
                if (instrument.Meter.Name == "Test")
                    listener.EnableMeasurementEvents(instrument);
            };

            double current = 0;
            int measurements = 0;

            meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => {
                if (instrument.Name == "gauge") {
                    current = measurement;
                    measurements++;
                }
            });
            meterListener.Start();

            _client.Gauge("gauge", 1.1);

            meterListener.RecordObservableInstruments();

            Assert.Equal(1.1, current);
            Assert.Equal(1, measurements);
        }

        [Fact]
        public void Timer() {
            using var meterListener = new MeterListener();
            meterListener.InstrumentPublished = (instrument, listener) => {
                if (instrument.Meter.Name == "Test")
                    listener.EnableMeasurementEvents(instrument);
            };

            int total = 0;
            int measurements = 0;

            meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) => {
                if (instrument.Name == "timer") {
                    measurements++;
                    total += measurement;
                }
            });
            meterListener.Start();

            _client.Timer("timer", 450);
            _client.Timer("timer", 220);

            Thread.Sleep(100);

            Assert.Equal(670, total);
            Assert.Equal(2, measurements);
        }

        public void Dispose() {
            _client.Dispose();
        }
    }
}