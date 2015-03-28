using System;
using System.Diagnostics;

namespace Foundatio.Metrics {
    public class MetricTimer : IDisposable {
        private readonly string _name;
        private readonly Stopwatch _stopWatch;
        private bool _disposed;
        private readonly IMetricsClient _client;

        public MetricTimer(string name, IMetricsClient client) {
            _name = name;
            _stopWatch = new Stopwatch();
            _client = client;
            _stopWatch.Start();
        }

        public void Dispose() {
            if (_disposed)
                return;

            _disposed = true;
            _stopWatch.Stop();
            _client.TimerAsync(_name, _stopWatch.ElapsedMilliseconds).Wait();
        }
    }
}