using System.Threading.Tasks;

namespace Foundatio.Metrics {
    public class NullMetricsClient : IMetricsClient {
        public static readonly IMetricsClient Instance = new NullMetricsClient();
        public void Counter(string name, int value = 1) {}
        public void Gauge(string name, double value) {}
        public void Timer(string name, int milliseconds) {}
        public void Dispose() {}
    }
}
