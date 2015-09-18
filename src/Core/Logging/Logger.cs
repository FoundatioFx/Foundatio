using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Foundatio.Logging {
    /// <summary>
    /// A simple logging class
    /// </summary>
    [DebuggerStepThrough]
    public static class Logger {
        private static Action<LogData> _logWriter;
        private static LogLevel _minimumLogLevel = LogLevel.Trace;
        private static readonly object _writerLock;

        private static readonly ThreadLocal<IDictionary<string, string>> _threadProperties;
        private static readonly Lazy<IDictionary<string, string>> _globalProperties;

        /// <summary>
        /// Initializes the <see cref="Logger"/> class.
        /// </summary>
        static Logger() {
            _writerLock = new object();
            _logWriter = DebugWrite;

            _globalProperties = new Lazy<IDictionary<string, string>>(CreateGlobal);
            _threadProperties = new ThreadLocal<IDictionary<string, string>>(CreateLocal);
        }
        
        /// <summary>
        /// Gets the global properties dictionary.  All values are copied to each log on write.
        /// </summary>
        /// <value>
        /// The global properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public static IDictionary<string, string> GlobalProperties => _globalProperties.Value;

        /// <summary>
        /// Gets the thread-local properties dictionary.  All values are copied to each log on write.
        /// </summary>
        /// <value>
        /// The thread-local properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public static IDictionary<string, string> ThreadProperties => _threadProperties.Value;

        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the specified <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevel">The log level.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        public static ILogBuilder Log(LogLevel logLevel, [CallerFilePath] string callerFilePath = null) {
            return CreateBuilder(logLevel, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the computed <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevelFactory">The log level factory.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>hrough]
        public static ILogBuilder Log(Func<LogLevel> logLevelFactory, [CallerFilePath] string callerFilePath = null) {
            var logLevel = (logLevelFactory != null) ? logLevelFactory() : LogLevel.Debug;

            return CreateBuilder(logLevel, callerFilePath);
        }


        /// <summary>
        /// Start a fluent <see cref="LogLevel.Trace"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Trace([CallerFilePath] string callerFilePath = null) {
            return CreateBuilder(LogLevel.Trace, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Debug"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Debug([CallerFilePath] string callerFilePath = null) {
            return CreateBuilder(LogLevel.Debug, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Info"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Info([CallerFilePath] string callerFilePath = null) {
            return CreateBuilder(LogLevel.Info, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Warn"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Warn([CallerFilePath] string callerFilePath = null) {
            return CreateBuilder(LogLevel.Warn, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Error"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Error([CallerFilePath] string callerFilePath = null) {
            return CreateBuilder(LogLevel.Error, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Fatal"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Fatal([CallerFilePath] string callerFilePath = null) {
            return CreateBuilder(LogLevel.Fatal, callerFilePath);
        }

        /// <summary>
        /// Set the global minimum log level.
        /// </summary>
        /// <param name="level">The minimum log level that will be logged.</param>
        public static void SetMinimumLogLevel(LogLevel level) {
            _minimumLogLevel = level;
        }

        /// <summary>
        /// Registers a <see langword="delegate"/> to write logs to.
        /// </summary>
        /// <param name="writer">The <see langword="delegate"/> to write logs to.</param>
        public static void RegisterWriter(Action<LogData> writer) {
            lock (_writerLock)
                _logWriter = writer;
        }
        
        private static Action<LogData> ResolveWriter() {
            lock (_writerLock)
                return _logWriter;
        }
        
        private static void DebugWrite(LogData logData) {
            System.Diagnostics.Debug.WriteLine(logData);
        }
        
        private static ILogBuilder CreateBuilder(LogLevel logLevel, string callerFilePath) {
            if (logLevel < _minimumLogLevel || logLevel == LogLevel.None)
                return new NullLogBuilder();

            string name = LoggerExtensions.GetFileNameWithoutExtension(callerFilePath ?? String.Empty);

            var writer = ResolveWriter();
            var builder = new LogBuilder(logLevel, writer);
            builder.Logger(name);

            MergeProperties(builder);

            return builder;
        }
        
        private static IDictionary<string, string> CreateLocal() {
            var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dictionary.Add("ThreadId", Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));

            return dictionary;
        }
        
        private static IDictionary<string, string> CreateGlobal() {
            var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dictionary.Add("MachineName", Environment.MachineName);

            return dictionary;
        }

        private static void MergeProperties(LogBuilder builder) {
            // copy global properties to current builder only if it has been created
            if (_globalProperties.IsValueCreated && _globalProperties.Value.Count > 0)
                foreach (var pair in _globalProperties.Value)
                    builder.Property(pair.Key, pair.Value);

            // copy thread-local properties to current builder only if it has been created
            if (_threadProperties.IsValueCreated && _threadProperties.Value.Count > 0)
                foreach (var pair in _threadProperties.Value)
                    builder.Property(pair.Key, pair.Value);
        }
    }
}