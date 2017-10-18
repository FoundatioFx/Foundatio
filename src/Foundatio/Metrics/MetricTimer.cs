using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public class MetricTimer : IAsyncDisposable {
        private readonly string _name;
        private readonly Stopwatch _stopWatch;
        private bool _disposed;
        private readonly IMetricsClient _client;

        public MetricTimer(string name, IMetricsClient client) {
            _name = name;
            _client = client;
            _stopWatch = Stopwatch.StartNew();
        }

        public Task DisposeAsync() {
            if (_disposed)
                return Task.CompletedTask;

            _disposed = true;
            _stopWatch.Stop();
            return _client.TimerAsync(_name, (int)_stopWatch.ElapsedMilliseconds);
        }
    }
}