using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
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
            _client = new StatsDMetricsClient("127.0.0.1", _port, "test");
        }

        [Fact]
        public async Task CounterAsync() {
            await StartListeningAsync(1);
            await _client.CounterAsync("counter");
            var messages = GetMessages();
            Assert.Equal("test.counter:1|c", messages.FirstOrDefault());
        }

        [Fact]
        public async Task CounterAsyncWithValue() {
            await StartListeningAsync(1);

            await _client.CounterAsync("counter", 5);
            var messages = GetMessages();
            Assert.Equal("test.counter:5|c", messages.FirstOrDefault());
        }

        [Fact]
        public async Task GaugeAsync() {
            await StartListeningAsync(1);

            await _client.GaugeAsync("gauge", 1.1);
            var messages = GetMessages();
            Assert.Equal("test.gauge:1.1|g", messages.FirstOrDefault());
        }

        [Fact]
        public async Task TimerAsync() {
            await StartListeningAsync(1);

            await _client.TimerAsync("timer", 1);
            var messages = GetMessages();
            Assert.Equal("test.timer:1|ms", messages.FirstOrDefault());
        }

        [Fact]
        public async Task CanSendOffline() {
            await _client.CounterAsync("counter");
            var messages = GetMessages();
            Assert.Equal(0, messages.Count);
        }

        [Fact]
        public async Task CanSendMultithreaded() {
            const int iterations = 100;
            await StartListeningAsync(iterations);

            await Run.InParallel(iterations, async i =>{
                await Task.Delay(50);
                await _client.CounterAsync("counter");
            });
            
            var messages = GetMessages();
            Assert.Equal(iterations, messages.Count);
        }

        [Fact(Skip = "Flakey")]
        public async Task CanSendMultiple() {
            const int iterations = 100000;
            await StartListeningAsync(iterations);

            var metrics = new InMemoryMetricsClient();

            var sw = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++) {
                if (index % (iterations / 10) == 0)
                    StopListening();

                await _client.CounterAsync("counter");
                await metrics.CounterAsync("counter");

                if (index % (iterations / 10) == 0)
                    await StartListeningAsync(iterations - index);

                if (index % (iterations / 20) == 0)
                    _logger.Trace((await metrics.GetCounterStatsAsync("counter")).ToString());
            }

            sw.Stop();
            _logger.Info((await metrics.GetCounterStatsAsync("counter")).ToString());

            // Require at least 10,000 operations/s
            Assert.InRange(sw.ElapsedMilliseconds, 0, (iterations / 10000.0) * 1000);

            await Task.Delay(250);
            var messages = GetMessages();
            int expected = iterations - (iterations / (iterations / 10));
            Assert.InRange(messages.Count, expected - 10, expected + 10);
            foreach (string message in messages)
                Assert.Equal("test.counter:1|c", message);
        }

        private List<string> GetMessages() {
            while (_listenerThread != null && _listenerThread.IsAlive) {}

            return _listener.GetMessages();
        }

        private async Task StartListeningAsync(int expectedMessageCount) {
            _listenerThread = new Thread(_listener.StartListening) { IsBackground = true };
            _listenerThread.Start(expectedMessageCount);

            await Task.Delay(75);
        }

        private void StopListening() {
            _listenerThread.Abort();
        }

        public void Dispose() {
            _listener.Dispose();
        }
    }
}