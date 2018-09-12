using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public class StatsDMetricsTests : TestWithLoggingBase, IDisposable {
        private readonly int _port = new Random(12345).Next(10000, 15000);
        private readonly StatsDMetricsClient _client;
        private readonly TestUdpListener _listener;

        public StatsDMetricsTests(ITestOutputHelper output) : base(output) {
            _listener = new TestUdpListener("224.0.0.1", _port);
            _client = new StatsDMetricsClient(o => o.Server("224.0.0.1", _port).Prefix("test"));
        }

        [Fact]
        public void Counter() {
            _listener.StartListening();
            _client.Counter("counter");
            _listener.StopListening(1);
            var messages = _listener.GetMessages();
            Assert.Single(messages);
            Assert.Equal("test.counter:1|c", messages.First());
        }

        [Fact]
        public void CounterWithValue() {
            _listener.StartListening();

            _client.Counter("counter", 5);
            _listener.StopListening(1);
            var messages = _listener.GetMessages();
            Assert.Single(messages);
            Assert.Equal("test.counter:5|c", messages.First());
        }

        [Fact]
        public void Gauge() {
            _listener.StartListening();

            _client.Gauge("gauge", 1.1);
            _listener.StopListening(1);

            var messages = _listener.GetMessages();
            Assert.Single(messages);
            Assert.Equal("test.gauge:1.1|g", messages.First());
        }

        [Fact]
        public void Timer() {
            _listener.StartListening();

            _client.Timer("timer", 1);
            _listener.StopListening(1);
            var messages = _listener.GetMessages();
            Assert.Single(messages);
            Assert.Equal("test.timer:1|ms", messages.First());
        }

        [Fact]
        public void CanSendOffline() {
            _client.Counter("counter");
            var messages = _listener.GetMessages();
            Assert.Empty(messages);
        }

        [Fact]
        public void CanSendMultithreaded() {
            const int iterations = 100;
            _listener.StartListening();

            Parallel.For(0, iterations, i => {
                SystemClock.Sleep(50);
                _client.Counter("counter");
            });
            
            _listener.StopListening(iterations);
            var messages = _listener.GetMessages();
            Assert.Equal(iterations, messages.Length);
        }

        [Fact]
        public void CanSendMultiple() {
            const int iterations = 100000;
            _listener.StartListening();

            var sw = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++) {
                if (index % (iterations / 10) == 0)
                    _listener.StopListening();

                _client.Counter("counter");

                if (index % (iterations / 10) == 0)
                    _listener.StartListening();
            }

            sw.Stop();
            // Require at least 1,000 operations/s
            Assert.InRange(sw.ElapsedMilliseconds, 0, (iterations / 1000.0) * 1000);

            _listener.StopListening(iterations);
            var messages = _listener.GetMessages();
            Assert.InRange(messages.Length, iterations * 0.9, iterations);
            foreach (string message in messages)
                Assert.Equal("test.counter:1|c", message);
        }

        public void Dispose() {
            _listener.Dispose();
        }
    }
}