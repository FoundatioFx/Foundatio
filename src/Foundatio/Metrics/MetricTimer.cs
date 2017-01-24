using System;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public class MetricTimer : IAsyncDisposable {
        private readonly string _name;
        private readonly IStopwatch _stopWatch;
        private bool _disposed;
        private readonly IMetricsClient _client;

        public MetricTimer(string name, IMetricsClient client) {
            _name = name;
            _client = client;
            _stopWatch = SystemClock.Instance.StartStopwatch();
        }

        public async Task DisposeAsync() {
            if (_disposed)
                return;

            _disposed = true;
            await _client.TimerAsync(_name, (int)_stopWatch.Elapsed.TotalMilliseconds).AnyContext();
        }
    }
}