namespace Foundatio.Metrics {
    public class InMemoryMetricsClientOptions : SharedMetricsClientOptions { }

    public class InMemoryMetricsClientOptionsBuilder : OptionsBuilder<InMemoryMetricsClientOptions>, ISharedMetricsClientOptionsBuilder {}
}