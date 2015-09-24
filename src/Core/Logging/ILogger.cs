using System;

namespace Foundatio.Logging {
    /// <summary>
    /// A logger <see langword="interface"/> for starting log messages.
    /// </summary>
    public interface ILogger {
        /// <summary>
        /// Gets the logger name.
        /// </summary>
        /// <value>
        /// The logger name.
        /// </value>
        string Name { get; }

        /// <summary>
        /// Gets the logger initial default properties.  All values are copied to each log.
        /// </summary>
        /// <value>
        /// The logger initial default properties.
        /// </value>
        IPropertyContext Properties { get; }


        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the specified <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevel">The log level.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder Log(LogLevel logLevel);

        /// <summary>
        /// Start a fluent <see cref="LogBuilder" /> with the computed <see cref="LogLevel" />.
        /// </summary>
        /// <param name="logLevelFactory">The log level factory.</param>
        /// <returns>
        /// A fluent Logger instance.
        /// </returns>
        ILogBuilder Log(Func<LogLevel> logLevelFactory);

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Trace"/> logger.
        /// </summary>
        /// <returns>A fluent Logger instance.</returns>
        ILogBuilder Trace();

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Debug"/> logger.
        /// </summary>
        /// <returns>A fluent Logger instance.</returns>
        ILogBuilder Debug();

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Info"/> logger.
        /// </summary>
        /// <returns>A fluent Logger instance.</returns>
        ILogBuilder Info();

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Warn"/> logger.
        /// </summary>
        /// <returns>A fluent Logger instance.</returns>
        ILogBuilder Warn();

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Error"/> logger.
        /// </summary>
        /// <returns>A fluent Logger instance.</returns>
        ILogBuilder Error();

        /// <summary>
        /// Start a fluent <see cref="LogLevel.Fatal"/> logger.
        /// </summary>
        /// <returns>A fluent Logger instance.</returns>
        ILogBuilder Fatal();
    }
}