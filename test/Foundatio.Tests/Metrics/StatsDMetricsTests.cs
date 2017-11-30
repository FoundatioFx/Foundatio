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
        private readonly UdpListener _listener;
        private Thread _listenerThread;

        public StatsDMetricsTests(ITestOutputHelper output) : base(output) {
            _listener = new UdpListener("127.0.0.1", _port);
            _client = new StatsDMetricsClient(new StatsDMetricsClientOptions { ServerName = "127.0.0.1", Port = _port, Prefix = "test" });
        }

        [Fact]
        public void Counter() {
            StartListening(1);
            _client.Counter("counter");
            var messages = GetMessages();
            Assert.Equal("test.counter:1|c", messages.FirstOrDefault());
        }

        [Fact]
        public void CounterWithValue() {
            StartListening(1);

            _client.Counter("counter", 5);
            var messages = GetMessages();
            Assert.Equal("test.counter:5|c", messages.FirstOrDefault());
        }

        [Fact]
        public void Gauge() {
            StartListening(1);

            _client.Gauge("gauge", 1.1);
            var messages = GetMessages();
            Assert.Equal("test.gauge:1.1|g", messages.FirstOrDefault());
        }

        [Fact]
        public void Timer() {
            StartListening(1);

            _client.Timer("timer", 1);
            var messages = GetMessages();
            Assert.Equal("test.timer:1|ms", messages.FirstOrDefault());
        }

        [Fact]
        public void CanSendOffline() {
            _client.Counter("counter");
            var messages = GetMessages();
            Assert.Empty(messages);
        }

        [Fact]
        public void CanSendMultithreaded() {
            const int iterations = 100;
            StartListening(iterations);

            Parallel.For(0, iterations, i => {
                SystemClock.Sleep(50);
                _client.Counter("counter");
            });
            
            var messages = GetMessages();
            Assert.Equal(iterations, messages.Count);
        }

        [Fact]
        public async Task CanSendMultiple() {
            const int iterations = 100000;
            StartListening(iterations);

            var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions());

            var sw = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++) {
                if (index % (iterations / 10) == 0)
                    StopListening();

                _client.Counter("counter");
                metrics.Counter("counter");

                if (index % (iterations / 10) == 0)
                    StartListening(iterations - index);

                if (index % (iterations / 20) == 0 && _logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace((await metrics.GetCounterStatsAsync("counter")).ToString());
            }

            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation((await metrics.GetCounterStatsAsync("counter")).ToString());

            // Require at least 10,000 operations/s
            Assert.InRange(sw.ElapsedMilliseconds, 0, (iterations / 10000.0) * 1000);

            SystemClock.Sleep(250);
            var messages = GetMessages();
            int expected = iterations - (iterations / (iterations / 10));
            Assert.InRange(messages.Count, expected - 90, expected + 10);
            foreach (string message in messages)
                Assert.Equal("test.counter:1|c", message);
        }

        private List<string> GetMessages() {
            while (_listenerThread != null && _listenerThread.IsAlive) {}

            return _listener.GetMessages();
        }

        private void StartListening(int expectedMessageCount) {
            _listenerThread = new Thread(_listener.StartListening) { IsBackground = true };
            _listenerThread.Start(expectedMessageCount);

            SystemClock.Sleep(75);
        }

        private void StopListening() {
            _listener.StopListening();
        }

        public void Dispose() {
            _listener.Dispose();
        }
    }
}