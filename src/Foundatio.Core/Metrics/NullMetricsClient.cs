using System;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public class NullMetricsClient : IMetricsClient {
        public static readonly IMetricsClient Instance = new NullMetricsClient();

        public Task CounterAsync(string name, int value = 1) {
            return TaskHelper.Completed;
        }

        public Task GaugeAsync(string name, double value) {
            return TaskHelper.Completed;
        }

        public Task TimerAsync(string name, int milliseconds) {
            return TaskHelper.Completed;
        }

        public void Dispose() {}
    }
}
