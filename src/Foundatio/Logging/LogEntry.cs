using System;
using System.Collections.Generic;

namespace Foundatio.Logging {
    public class LogEntry {
        public DateTime Date { get; set; }
        public string CategoryName { get; set; }
        public LogLevel LogLevel { get; set; }
        public object[] Scopes { get; set; }
        public EventId EventId { get; set; }
        public object State { get; set; }
        public Exception Exception { get; set; }
        public string Message { get; set; }
        public IDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public override string ToString() {
            return String.Concat("", Date.ToString("mm:ss.fffff"), " ", LogLevel.ToString().Substring(0, 1).ToUpper(), ":", CategoryName, " - ", Message);
        }

        public string ToString(bool useFullCategory) {
            var category = CategoryName;
            if (!useFullCategory) {
                var lastDot = category.LastIndexOf('.');
                if (lastDot >= 0)
                    category = category.Substring(lastDot + 1);
            }

            return String.Concat("", Date.ToString("mm:ss.fffff"), " ", LogLevel.ToString().Substring(0, 1).ToUpper(), ":", category, " - ", Message);
        }
    }
}