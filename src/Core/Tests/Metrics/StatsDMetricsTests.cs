using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public class StatsDMetricsTests : IDisposable {
        private readonly TestOutputWriter _writer;
        private readonly int _port = new Random(12345).Next(10000, 15000);
        private readonly StatsDMetricsClient _client;
        private readonly UdpListener _listener;
        private Thread _listenerThread;

        public StatsDMetricsTests(ITestOutputHelper output) {
            _listener = new UdpListener("127.0.0.1", _port);
            _client = new StatsDMetricsClient("127.0.0.1", _port, "test");

            _writer = new TestOutputWriter(output);
        }

        [Fact]
        public async Task CounterAsync() {
            StartListening(1);
            await _client.CounterAsync("counter");
            var messages = GetMessages();
            Assert.Equal("test.counter:1|c", messages.FirstOrDefault());
        }

        [Fact]
        public async Task CounterAsyncWithValue() {
            StartListening(1);

            await _client.CounterAsync("counter", 5);
            var messages = GetMessages();
            Assert.Equal("test.counter:5|c", messages.FirstOrDefault());
        }

        [Fact]
        public async Task GaugeAsync() {
            StartListening(1);

            await _client.GaugeAsync("gauge", 1.1);
            var messages = GetMessages();
            Assert.Equal("test.gauge:1.1|g", messages.FirstOrDefault());
        }

        [Fact]
        public async Task TimerAsync() {
            StartListening(1);

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
        public void CanSendMultithreaded() {
            const int iterations = 100;
            StartListening(iterations);
            
            var result = Parallel.For(0, iterations, async i => {
                Thread.Sleep(50);
                await _client.CounterAsync("counter");
            });

            while (!result.IsCompleted) {}

            var messages = GetMessages();
            Assert.Equal(iterations, messages.Count);
        }

        [Fact]
        public async Task CanSendMultiple() {
            const int iterations = 100000;
            StartListening(iterations);

            var metrics = new InMemoryMetricsClient();
            var sw = new Stopwatch();
            sw.Start();
            for (int index = 0; index < iterations; index++) {
                if (index % (iterations / 10) == 0)
                    StopListening();

                await _client.CounterAsync("counter");
                await metrics.CounterAsync("counter");

                if (index % (iterations / 10) == 0)
                    StartListening(iterations - index);

                if (index % (iterations / 20) == 0)
                    metrics.DisplayStats(_writer);
            }

            sw.Stop();
            metrics.DisplayStats(_writer);

            // Require at least 10,000 operations/s
            Assert.InRange(sw.ElapsedMilliseconds, 0, (iterations / 10000.0) * 1000);

            Thread.Sleep(250);
            var messages = GetMessages();
            Assert.Equal(iterations - (iterations / (iterations / 10)), messages.Count);
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

            Thread.Sleep(75);
        }

        private void StopListening() {
            _listenerThread.Abort();
        }

        public void Dispose() {
            _listener.Dispose();
        }
    }
}