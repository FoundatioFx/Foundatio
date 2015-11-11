using System;
using Foundatio.Metrics;

namespace Foundatio.Tests.Queue {
    public class SimpleWorkItem : IHaveMetricName {
        public string Data { get; set; }
        public int Id { get; set; }

        public string GetMetricName() {
            return Data.Trim();
        }
    }
}
