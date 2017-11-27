using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Foundatio.Logging.Xunit
{
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
            string category = CategoryName;
            if (!useFullCategory) {
                int lastDot = category.LastIndexOf('.');
                if (lastDot >= 0)
                    category = category.Substring(lastDot + 1);
            }

            return String.Concat("", Date.ToString("mm:ss.fffff"), " ", LogLevel.ToString().Substring(0, 1).ToUpper(), ":", category, " - ", Message);
        }
    }
}