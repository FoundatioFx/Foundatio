using System;
using System.Diagnostics;
using Foundatio.Extensions;

namespace Foundatio.Metrics {
    public class MetricTimer : IDisposable {
        private readonly string _name;
        private readonly Stopwatch _stopWatch;
        private bool _disposed;
        private readonly IMetricsClient _client;

        public MetricTimer(string name, IMetricsClient client) {
            _name = name;
            _client = client;
            _stopWatch = Stopwatch.StartNew();
        }

        public async void Dispose() {
            if (_disposed)
                return;

            _disposed = true;
            _stopWatch.Stop();
            await _client.TimerAsync(_name, _stopWatch.ElapsedMilliseconds).AnyContext();
        }
    }
}