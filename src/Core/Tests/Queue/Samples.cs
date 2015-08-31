using System;
using System.Text.RegularExpressions;
using Foundatio.Metrics;

namespace Foundatio.Tests.Queue {
    public class SimpleWorkItem : IHaveMetricName {
        public string Data { get; set; }
        public int Id { get; set; }

        public string GetMetricName()
        {
            return Regex.Replace(Data, "\\W+", string.Empty);
        }
    }
}
