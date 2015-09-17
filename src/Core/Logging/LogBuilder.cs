using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Foundatio.Logging {
    [DebuggerStepThrough]
    public sealed class LogBuilder : ILogBuilder {
        private readonly LogData _data;
        private readonly Action<LogData> _writer;

        public LogBuilder(LogLevel logLevel, Action<LogData> writer) {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            _writer = writer;
            _data = new LogData {
                LogLevel = logLevel,
                FormatProvider = CultureInfo.InvariantCulture,
                Logger = typeof(Logger).FullName
            };
        }

        public LogData LogData => _data;

        public ILogBuilder Level(LogLevel logLevel) {
            _data.LogLevel = logLevel;
            return this;
        }

        public ILogBuilder Logger(string logger) {
            _data.Logger = logger;

            return this;
        }

        public ILogBuilder Logger<TLogger>() {
            _data.Logger = typeof(TLogger).FullName;

            return this;
        }

        public ILogBuilder Message(string message) {
            _data.Message = message;

            return this;
        }

        public ILogBuilder Message(string format, object arg0) {
            _data.Message = format;
            _data.Parameters = new[] { arg0 };

            return this;
        }

        public ILogBuilder Message(string format, object arg0, object arg1) {
            _data.Message = format;
            _data.Parameters = new[] { arg0, arg1 };

            return this;
        }

        public ILogBuilder Message(string format, object arg0, object arg1, object arg2) {
            _data.Message = format;
            _data.Parameters = new[] { arg0, arg1, arg2 };

            return this;
        }

        public ILogBuilder Message(string format, object arg0, object arg1, object arg2, object arg3) {
            _data.Message = format;
            _data.Parameters = new[] { arg0, arg1, arg2, arg3 };

            return this;
        }

        public ILogBuilder Message(string format, params object[] args) {
            _data.Message = format;
            _data.Parameters = args;

            return this;
        }

        public ILogBuilder Message(IFormatProvider provider, string format, params object[] args) {
            _data.FormatProvider = provider;
            _data.Message = format;
            _data.Parameters = args;

            return this;
        }

        public ILogBuilder Property(string name, object value) {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (_data.Properties == null)
                _data.Properties = new Dictionary<string, object>();

            _data.Properties[name] = value;
            return this;
        }

        public ILogBuilder Exception(Exception exception) {
            _data.Exception = exception;
            return this;
        }

        public void Write([CallerMemberName] string callerMemberName = null, [CallerFilePath] string callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) {
            if (callerMemberName != null)
                _data.MemberName = callerMemberName;
            if (callerFilePath != null)
                _data.FilePath = callerFilePath;
            if (callerLineNumber != 0)
                _data.LineNumber = callerLineNumber;

            _writer(_data);
        }
        
        public void WriteIf(Func<bool> condition, [CallerMemberName] string callerMemberName = null, [CallerFilePath] string callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) {
            if (condition == null || !condition())
                return;

            Write(callerMemberName, callerFilePath, callerLineNumber);
        }

        public void WriteIf(bool condition, [CallerMemberName] string callerMemberName = null, [CallerFilePath] string callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) {
            if (!condition)
                return;

            Write(callerMemberName, callerFilePath, callerLineNumber);
        }
    }
}