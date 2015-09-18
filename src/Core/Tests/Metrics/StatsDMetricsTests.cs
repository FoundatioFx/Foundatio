﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public class StatsDMetricsTests : CaptureTests, IDisposable {
        private readonly int _port = new Random(12345).Next(10000, 15000);
        private readonly StatsDMetricsClient _client;
        private readonly UdpListener _listener;
        private Thread _listenerThread;

        public StatsDMetricsTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {
            _listener = new UdpListener("127.0.0.1", _port);
            _client = new StatsDMetricsClient("127.0.0.1", _port, "test");
        }

        [Fact]
        public async Task CounterAsync() {
            await StartListeningAsync(1).AnyContext();
            await _client.CounterAsync("counter").AnyContext();
            var messages = GetMessages();
            Assert.Equal("test.counter:1|c", messages.FirstOrDefault());
        }

        [Fact]
        public async Task CounterAsyncWithValue() {
            await StartListeningAsync(1).AnyContext();

            await _client.CounterAsync("counter", 5).AnyContext();
            var messages = GetMessages();
            Assert.Equal("test.counter:5|c", messages.FirstOrDefault());
        }

        [Fact]
        public async Task GaugeAsync() {
            await StartListeningAsync(1).AnyContext();

            await _client.GaugeAsync("gauge", 1.1).AnyContext();
            var messages = GetMessages();
            Assert.Equal("test.gauge:1.1|g", messages.FirstOrDefault());
        }

        [Fact]
        public async Task TimerAsync() {
            await StartListeningAsync(1).AnyContext();

            await _client.TimerAsync("timer", 1).AnyContext();
            var messages = GetMessages();
            Assert.Equal("test.timer:1|ms", messages.FirstOrDefault());
        }

        [Fact]
        public async Task CanSendOffline() {
            await _client.CounterAsync("counter").AnyContext();
            var messages = GetMessages();
            Assert.Equal(0, messages.Count);
        }

        [Fact]
        public async Task CanSendMultithreaded() {
            const int iterations = 100;
            await StartListeningAsync(iterations).AnyContext();

            await Run.InParallel(iterations, async i =>{
                await Task.Delay(50).AnyContext();
                await _client.CounterAsync("counter").AnyContext();
            }).AnyContext();
            
            var messages = GetMessages();
            Assert.Equal(iterations, messages.Count);
        }

        [Fact]
        public async Task CanSendMultiple() {
            const int iterations = 100000;
            await StartListeningAsync(iterations).AnyContext();

            var metrics = new InMemoryMetricsClient();

            var sw = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++) {
                if (index % (iterations / 10) == 0)
                    StopListening();

                await _client.CounterAsync("counter").AnyContext();
                await metrics.CounterAsync("counter").AnyContext();

                if (index % (iterations / 10) == 0)
                    await StartListeningAsync(iterations - index).AnyContext();

                if (index % (iterations / 20) == 0)
                    metrics.DisplayStats(_writer);
            }

            sw.Stop();
            metrics.DisplayStats(_writer);

            // Require at least 10,000 operations/s
            Assert.InRange(sw.ElapsedMilliseconds, 0, (iterations / 10000.0) * 1000);

            await Task.Delay(250).AnyContext();
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

            await Task.Delay(75).AnyContext();
        }

        private void StopListening() {
            _listenerThread.Abort();
        }

        public new void Dispose() {
            _listener.Dispose();
            base.Dispose();
        }
    }
}