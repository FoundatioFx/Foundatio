using System;
using System.Text.RegularExpressions;
using Foundatio.Metrics;

namespace Foundatio.Tests.Queue {
    public class SimpleWorkItem : IMetricStatName {
        public string Data { get; set; }
        public int Id { get; set; }

        public string GetStatName()
        {
            return Regex.Replace(Data, "\\W+", string.Empty);
        }
    }
}
