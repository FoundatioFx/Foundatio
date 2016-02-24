using System;
using System.Runtime.CompilerServices;

namespace Foundatio.Logging {
    public sealed class NullLogBuilder : ILogBuilder {
        public static readonly ILogBuilder Instance = new NullLogBuilder();

        public LogData LogData => null;

        public ILogBuilder Level(LogLevel logLevel) {
            return this;
        }

        public ILogBuilder Message(Func<string> messageFormatter)
        {
            return this;
        }

        public ILogBuilder Message(string message) {
            return this;
        }

        public ILogBuilder Message(string format, object arg0) {
            return this;
        }

        public ILogBuilder Message(string format, object arg0, object arg1) {
            return this;
        }

        public ILogBuilder Message(string format, object arg0, object arg1, object arg2) {
            return this;
        }

        public ILogBuilder Message(string format, object arg0, object arg1, object arg2, object arg3) {
            return this;
        }

        public ILogBuilder Message(string format, params object[] args) {
            return this;
        }

        public ILogBuilder Message(IFormatProvider provider, string format, params object[] args) {
            return this;
        }

        public ILogBuilder Property(string name, object value) {
            return this;
        }

        public ILogBuilder Exception(Exception exception) {
            return this;
        }

        public void Write([CallerMemberName] string callerMemberName = null, [CallerFilePath] string callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) { }

        public void WriteIf(Func<bool> condition, [CallerMemberName] string callerMemberName = null, [CallerFilePath] string callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) { }

        public void WriteIf(bool condition, [CallerMemberName] string callerMemberName = null, [CallerFilePath] string callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) { }
    }
}