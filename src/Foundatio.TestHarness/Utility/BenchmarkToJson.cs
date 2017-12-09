using System.Text;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;

namespace Foundatio.TestHarness.Utility {
    public class StringBenchmarkLogger : ILogger {
        private readonly StringBuilder _buffer = new StringBuilder();

        public void Write(LogKind logKind, string text) {
            _buffer.Append(text);
        }

        public void WriteLine() {
            _buffer.AppendLine();
        }

        public void WriteLine(LogKind logKind, string text) {
            _buffer.AppendLine(text);
        }

        public override string ToString() {
            return _buffer.ToString();
        }
    }

    public static class BenchmarkSummaryExtensions {
        public static string ToJson(this Summary summary, bool indentJson = true) {
            var exporter = new JsonExporter(indentJson: indentJson);
            var logger = new StringBenchmarkLogger();
            exporter.ExportToLog(summary, logger);
            return logger.ToString();
        }
    }
}
