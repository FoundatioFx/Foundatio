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
        private static readonly object _writerLock;
        private static Action<LogData> _logAction;
        private static ILogWriter _logWriter;
        private static bool _hasSearched;
        private static LogLevel _minimumLogLevel = LogLevel.Trace;

        // only create if used
        private static readonly ThreadLocal<IDictionary<string, string>> _threadProperties;
        private static readonly Lazy<IDictionary<string, string>> _globalProperties;

        private readonly Lazy<IDictionary<string, object>> _properties;


        /// <summary>
        /// Initializes the <see cref="Logger"/> class.
        /// </summary>
        static Logger() {
            _writerLock = new object();
            _logAction = DebugWrite;
            _hasSearched = false;

            _globalProperties = new Lazy<IDictionary<string, string>>(CreateGlobal);
            _threadProperties = new ThreadLocal<IDictionary<string, string>>(CreateLocal);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Logger"/> class.
        /// </summary>
        public Logger() {
            _properties = new Lazy<IDictionary<string, object>>(() => new Dictionary<string, object>());
        }


        /// <summary>
        /// Gets the global properties dictionary.  All values are copied to each log on write.
        /// </summary>
        /// <value>
        /// The global properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public static IDictionary<string, string> GlobalProperties {
            get { return _globalProperties.Value; }
        }

        /// <summary>
        /// Gets the thread-local properties dictionary.  All values are copied to each log on write.
        /// </summary>
        /// <value>
        /// The thread-local properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public static IDictionary<string, string> ThreadProperties {
            get { return _threadProperties.Value; }
        }


        /// <summary>
        /// Gets the logger initial default properties dictionary.  All values are copied to each log.
        /// </summary>
        /// <value>
        /// The logger initial default properties dictionary.
        /// </value>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public IDictionary<string, object> Properties {
            get { return _properties.Value; }
        }

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
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
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
            var logLevel = (logLevelFactory != null)
                ? logLevelFactory()
                : LogLevel.Debug;

            return CreateBuilder(logLevel, callerFilePath);
        }

        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the computed <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevelFactory">The log level factory.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
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
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
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
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
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
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
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
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
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
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is the file path at the time of compile.</param>
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
            lock (_writerLock) {
                _hasSearched = true;
                _logAction = writer;
            }
        }

        /// <summary>
        /// Registers a ILogWriter to write logs to.
        /// </summary>
        /// <param name="writer">The ILogWriter to write logs to.</param>
        public static void RegisterWriter<TWriter>(TWriter writer)
            where TWriter : ILogWriter {
            lock (_writerLock) {
                _hasSearched = true;
                _logWriter = writer;
            }
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
            return new Logger { Name = LoggerExtensions.GetFileNameWithoutExtension(callerFilePath ?? string.Empty) };
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
            lock (_writerLock) {
                SearchWriter();
                if (_logWriter != null)
                    return _logWriter.WriteLog;

                return _logAction ?? DebugWrite;
            }
        }

        private static void SearchWriter() {
            if (_hasSearched)
                return;

            _hasSearched = true;

            //search all assemblies for ILogWriter
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var a in assemblies) {
                Type[] types;

                try {
                    types = a.GetExportedTypes();
                } catch (ReflectionTypeLoadException e) {
                    types = e.Types.Where(t => t != null).ToArray();
                }

                var writerType = typeof(ILogWriter);
                var type = types.FirstOrDefault(t => !t.IsAbstract && writerType.IsAssignableFrom(t));
                if (type == null)
                    continue;

                _logWriter = Activator.CreateInstance(type) as ILogWriter;
                return;
            }
        }

        private static void DebugWrite(LogData logData) {
            System.Diagnostics.Debug.WriteLine(logData);
        }

        private static ILogBuilder CreateBuilder(LogLevel logLevel, string callerFilePath) {
            if (logLevel < _minimumLogLevel || logLevel == LogLevel.None)
                return new NullLogBuilder();

            string name = LoggerExtensions.GetFileNameWithoutExtension(callerFilePath ?? string.Empty);

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

        private static void MergeProperties(ILogBuilder builder) {
            // copy global properties to current builder only if it has been created
            if (_globalProperties.IsValueCreated)
                foreach (var pair in _globalProperties.Value)
                    builder.Property(pair.Key, pair.Value);

            // copy thread-local properties to current builder only if it has been created
            if (_threadProperties.IsValueCreated)
                foreach (var pair in _threadProperties.Value)
                    builder.Property(pair.Key, pair.Value);
        }


        private ILogBuilder MergeDefaults(ILogBuilder builder) {
            // copy logger name
            if (!string.IsNullOrEmpty(Name))
                builder.Logger(Name);

            // copy properties to current builder
            foreach (var pair in Properties)
                builder.Property(pair.Key, pair.Value);

            return builder;
        }
    }
}