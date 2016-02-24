using System;
using Foundatio.Logging;

namespace Foundatio.Tests.Utility {
    public class LogEntry {
        public DateTime Date { get; set; }
        public string CategoryName { get; set; }
        public LogLevel LogLevel { get; set; }
        public object[] Scope { get; set; }
        public EventId EventId { get; set; }
        public object State { get; set; }
        public Exception Exception { get; set; }

        public Func<object, Exception, string> Formatter { get; set; }

        public string GetMessage() {
            return Formatter(State, Exception);
        }

        public override string ToString() {
            return String.Concat("", Date.ToString("HH:mm:ss.fffff"), " ", LogLevel.ToString().Substring(0, 1).ToUpper(), ":", CategoryName, " - ", GetMessage());
        }
    }
}