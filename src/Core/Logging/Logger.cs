using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Foundatio.Logging {
    /// <summary>
    /// A logger class for starting log messages.
    /// </summary>
    [DebuggerStepThrough]
    public sealed class Logger : ILogger {
        private static Action<LogData> _logAction;
        private static ILogWriter _logWriter;
        private static LogLevel _minimumLogLevel = LogLevel.Trace;

        // only create if used
        private static readonly Lazy<IPropertyContext> _asyncProperties;
        private static readonly ThreadLocal<IPropertyContext> _threadProperties;
        private static readonly Lazy<IPropertyContext> _globalProperties;

        private readonly Lazy<IPropertyContext> _properties;


        /// <summary>
        /// Initializes the <see cref="Logger"/> class.
        /// </summary>
        static Logger() {
            _globalProperties = new Lazy<IPropertyContext>(CreateGlobal);
            _threadProperties = new ThreadLocal<IPropertyContext>(CreateLocal);
            _asyncProperties = new Lazy<IPropertyContext>(CreateAsync);

            _logWriter = new TraceLogWriter();
            _logAction = _logWriter.WriteLog;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Logger"/> class.
        /// </summary>
        public Logger() {
            _properties = new Lazy<IPropertyContext>(() => new PropertyContext());
        }


        /// <summary>
        /// Gets the global properties dictionary.  All values are copied to each log on write.
        /// </summary>
        /// <value>
        /// The global properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public static IPropertyContext GlobalProperties => _globalProperties.Value;

        /// <summary>
        /// Gets the thread-local properties dictionary.  All values are copied to each log on write.
        /// </summary>
        /// <value>
        /// The thread-local properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public static IPropertyContext ThreadProperties => _threadProperties.Value;

        /// <summary>
        /// Gets the property context that maintains state across asynchronous tasks and call contexts. All values are copied to each log on write.
        /// </summary>
        /// <value>
        /// The asynchronous property context.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public static IPropertyContext AsyncProperties => _asyncProperties.Value;

        /// <summary>
        /// Gets the logger initial default properties dictionary.  All values are copied to each log.
        /// </summary>
        /// <value>
        /// The logger initial default properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public IPropertyContext Properties => _properties.Value;

        /// <summary>
        /// Gets the logger name.
        /// </summary>
        /// <value>
        /// The logger name.
        /// </value>
        public string Name { get; set; }


        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the specified <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevel">The log level.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        public static ILogBuilder Log(LogLevel logLevel, [CallerFilePath]string callerFilePath = null) {
            return CreateBuilder(logLevel, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the specified <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevel">The log level.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Log(LogLevel logLevel) {
            var builder = Log(logLevel);
            return MergeDefaults(builder);
        }


        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the computed <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevelFactory">The log level factory.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        public static ILogBuilder Log(Func<LogLevel> logLevelFactory, [CallerFilePath]string callerFilePath = null) {
            var logLevel = logLevelFactory?.Invoke() ?? LogLevel.Debug;
            return CreateBuilder(logLevel, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the computed <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevelFactory">The log level factory.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Log(Func<LogLevel> logLevelFactory) {
            var builder = Log(logLevelFactory);
            return MergeDefaults(builder);
        }


        /// <summary>
        /// Start a fluent <see cref="LogLevel.Trace"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Trace([CallerFilePath]string callerFilePath = null) {
            return CreateBuilder(LogLevel.Trace, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Trace" /> logger.
        /// </summary>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Trace() {
            var builder = Trace();
            return MergeDefaults(builder);
        }


        /// <summary>
        /// Start a fluent <see cref="LogLevel.Debug"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Debug([CallerFilePath]string callerFilePath = null) {
            return CreateBuilder(LogLevel.Debug, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Debug" /> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Debug() {
            var builder = Debug();
            return MergeDefaults(builder);
        }


        /// <summary>
        /// Start a fluent <see cref="LogLevel.Info"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Info([CallerFilePath]string callerFilePath = null) {
            return CreateBuilder(LogLevel.Info, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Info" /> logger.
        /// </summary>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Info() {
            var builder = Info();
            return MergeDefaults(builder);
        }


        /// <summary>
        /// Start a fluent <see cref="LogLevel.Warn"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Warn([CallerFilePath]string callerFilePath = null) {
            return CreateBuilder(LogLevel.Warn, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Warn" /> logger.
        /// </summary>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Warn() {
            var builder = Warn();
            return MergeDefaults(builder);
        }


        /// <summary>
        /// Start a fluent <see cref="LogLevel.Error"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Error([CallerFilePath]string callerFilePath = null) {
            return CreateBuilder(LogLevel.Error, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Error" /> logger.
        /// </summary>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Error() {
            var builder = Error();
            return MergeDefaults(builder);
        }


        /// <summary>
        /// Start a fluent <see cref="LogLevel.Fatal"/> logger.
        /// </summary>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
        /// <returns>A fluent Logger instance.</returns>
        public static ILogBuilder Fatal([CallerFilePath]string callerFilePath = null) {
            return CreateBuilder(LogLevel.Fatal, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Fatal" /> logger.
        /// </summary>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder ILogger.Fatal() {
            var builder = Fatal();
            return MergeDefaults(builder);
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
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            var current = _logAction;
            Interlocked.CompareExchange(ref _logAction, writer, current);
        }

        /// <summary>
        /// Registers a ILogWriter to write logs to.
        /// </summary>
        /// <param name="writer">The ILogWriter to write logs to.</param>
        public static void RegisterWriter<TWriter>(TWriter writer)
            where TWriter : ILogWriter {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            var current = _logWriter;
            Interlocked.CompareExchange(ref _logWriter, writer, current);

            RegisterWriter(_logWriter.WriteLog);
        }


        /// <summary>
        /// Creates a new <see cref="ILogger"/> using the specified fluent <paramref name="builder"/> action.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns></returns>
        public static ILogger CreateLogger(Action<LoggerCreateBuilder> builder) {
            var factory = new Logger();
            var factoryBuilder = new LoggerCreateBuilder(factory);

            builder(factoryBuilder);

            return factory;
        }

        /// <summary>
        /// Creates a new <see cref="ILogger"/> using the caller file name as the logger name.
        /// </summary>
        /// <returns></returns>
        public static ILogger CreateLogger([CallerFilePath]string callerFilePath = null) {
            return new Logger { Name = GetName(callerFilePath) };
        }

        /// <summary>
        /// Creates a new <see cref="ILogger" /> using the specified type as the logger name.
        /// </summary>
        /// <param name="type">The type to use as the logger name.</param>
        /// <returns></returns>
        public static ILogger CreateLogger(Type type) {
            return new Logger { Name = type.FullName };
        }

        /// <summary>
        /// Creates a new <see cref="ILogger" /> using the specified type as the logger name.
        /// </summary>
        /// <typeparam name="T">The type to use as the logger name.</typeparam>
        /// <returns></returns>
        public static ILogger CreateLogger<T>() {
            return CreateLogger(typeof(T));
        }


        private static Action<LogData> ResolveWriter() {
            var writer = _logAction ?? DebugWrite;
            return writer;
        }

        private static void DebugWrite(LogData logData) {
            System.Diagnostics.Debug.WriteLine(logData);
        }

        private static ILogBuilder CreateBuilder(LogLevel logLevel, string callerFilePath) {
            if (logLevel < _minimumLogLevel || logLevel == LogLevel.None)
                return new NullLogBuilder();

            string name = GetName(callerFilePath);

            var writer = ResolveWriter();
            var builder = new LogBuilder(logLevel, writer);
            builder.Logger(name);

            MergeProperties(builder);

            return builder;
        }

        private static string GetName(string path) {
            if (path == null)
                return string.Empty;

            var parts = path.Split('\\', '/');
            var p = parts.LastOrDefault();
            if (p == null)
                return null;

            int length;
            if ((length = p.LastIndexOf('.')) == -1)
                return p;

            return p.Substring(0, length);
        }

        private static IPropertyContext CreateAsync() {
            var propertyContext = new AsynchronousContext();
            return propertyContext;
        }

        private static IPropertyContext CreateLocal() {
            var propertyContext = new PropertyContext();
            propertyContext.Set("ThreadId", Thread.CurrentThread.ManagedThreadId);

            return propertyContext;
        }

        private static IPropertyContext CreateGlobal() {
            var propertyContext = new PropertyContext();
            propertyContext.Set("MachineName", Environment.MachineName);

            return propertyContext;
        }

        private static void MergeProperties(ILogBuilder builder) {
            // copy global properties to current builder only if it has been created
            if (_globalProperties.IsValueCreated)
                _globalProperties.Value.Apply(builder);

            // copy thread-local properties to current builder only if it has been created
            if (_threadProperties.IsValueCreated)
                _threadProperties.Value.Apply(builder);

            // copy async properties to current builder only if it has been created
            if (_asyncProperties.IsValueCreated)
                _asyncProperties.Value.Apply(builder);
        }


        private ILogBuilder MergeDefaults(ILogBuilder builder) {
            // copy logger name
            if (!String.IsNullOrEmpty(Name))
                builder.Logger(Name);

            // copy properties to current builder
            if (_properties.IsValueCreated)
                _properties.Value.Apply(builder);

            return builder;
        }
    }
}