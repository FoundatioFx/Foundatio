using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
// ReSharper disable ExplicitCallerInfoArgument

namespace Foundatio.Logging {
    /// <summary>
    /// A fluent <see langword="interface"/> to build log messages.
    /// </summary>
    [DebuggerStepThrough]
    public sealed class LogBuilder : ILogBuilder {
        private readonly LogData _data;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LogBuilder" /> class.
        /// </summary>
        /// <param name="logLevel">The starting trace level.</param>
        /// <param name="logger">The delegate to write logs to.</param>
        /// <exception cref="System.ArgumentNullException">writer</exception>
        public LogBuilder(LogLevel logLevel, ILogger logger) {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            _logger = logger;
            _data = new LogData {
                LogLevel = logLevel,
                FormatProvider = CultureInfo.InvariantCulture
            };
        }

        /// <summary>
        /// Gets the log data that is being built.
        /// </summary>
        /// <value>
        /// The log data.
        /// </value>
        public LogData LogData => _data;

        /// <summary>
        /// Sets the id of the logging event.
        /// </summary>
        /// <param name="eventId">The id of the logging event.</param>
        /// <returns></returns>
        public ILogBuilder EventId(int eventId) {
            _data.EventId = eventId;
            return this;
        }

        /// <summary>
        /// Sets the log message on the logging event.
        /// </summary>
        /// <param name="message">The log message for the logging event.</param>
        /// <returns></returns>
        public ILogBuilder Message(string message) {
            _data.Message = message;
            return this;
        }
        
        /// <summary>
        /// Sets the log message on the logging event.
        /// </summary>
        /// <param name="messageFormatter">The message formatter <see langword="delegate"/>.</param>
        /// <returns></returns>
        public ILogBuilder Message(Func<string> messageFormatter) {
            _data.MessageFormatter = messageFormatter;
            return this;
        }

        /// <summary>
        /// Sets the log message and parameters for formating on the logging event.
        /// </summary>
        /// <param name="format">A composite format string.</param>
        /// <param name="arg0">The object to format.</param>
        /// <returns></returns>
        public ILogBuilder Message(string format, object arg0) {
            _data.Message = format;
            _data.Parameters = new[] { arg0 };
            return this;
        }

        /// <summary>
        /// Sets the log message and parameters for formating on the logging event.
        /// </summary>
        /// <param name="format">A composite format string.</param>
        /// <param name="arg0">The first object to format.</param>
        /// <param name="arg1">The second object to format.</param>
        /// <returns></returns>
        public ILogBuilder Message(string format, object arg0, object arg1) {
            _data.Message = format;
            _data.Parameters = new[] { arg0, arg1 };
            return this;
        }

        /// <summary>
        /// Sets the log message and parameters for formating on the logging event.
        /// </summary>
        /// <param name="format">A composite format string.</param>
        /// <param name="arg0">The first object to format.</param>
        /// <param name="arg1">The second object to format.</param>
        /// <param name="arg2">The third object to format.</param>
        /// <returns></returns>
        public ILogBuilder Message(string format, object arg0, object arg1, object arg2) {
            _data.Message = format;
            _data.Parameters = new[] { arg0, arg1, arg2 };
            return this;
        }

        /// <summary>
        /// Sets the log message and parameters for formating on the logging event.
        /// </summary>
        /// <param name="format">A composite format string.</param>
        /// <param name="arg0">The first object to format.</param>
        /// <param name="arg1">The second object to format.</param>
        /// <param name="arg2">The third object to format.</param>
        /// <param name="arg3">The fourth object to format.</param>
        /// <returns></returns>
        public ILogBuilder Message(string format, object arg0, object arg1, object arg2, object arg3) {
            _data.Message = format;
            _data.Parameters = new[] { arg0, arg1, arg2, arg3 };
            return this;
        }

        /// <summary>
        /// Sets the log message and parameters for formating on the logging event.
        /// </summary>
        /// <param name="format">A composite format string.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        /// <returns></returns>
        public ILogBuilder Message(string format, params object[] args) {
            _data.Message = format;
            _data.Parameters = args;
            return this;
        }

        /// <summary>
        /// Sets the log message and parameters for formating on the logging event.
        /// </summary>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="format">A composite format string.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        /// <returns></returns>
        public ILogBuilder Message(IFormatProvider provider, string format, params object[] args) {
            _data.FormatProvider = provider;
            _data.Message = format;
            _data.Parameters = args;
            return this;
        }

        /// <summary>
        /// Sets a log context property on the logging event.
        /// </summary>
        /// <param name="name">The name of the context property.</param>
        /// <param name="value">The value of the context property.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">name</exception>
        public ILogBuilder Property(string name, object value) {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (_data.Properties == null)
                _data.Properties = new Dictionary<string, object>();

            _data.Properties[name] = value;
            return this;
        }

        /// <summary>
        /// Sets the exception information of the logging event.
        /// </summary>
        /// <param name="exception">The exception information of the logging event.</param>
        /// <returns></returns>
        public ILogBuilder Exception(Exception exception) {
            _data.Exception = exception;
            return this;
        }

        /// <summary>
        /// Writes the log event to the underlying logger.
        /// </summary>
        /// <param name="callerMemberName">The method or property name of the caller to the method. This is set at by the compiler.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is set at by the compiler.</param>
        /// <param name="callerLineNumber">The line number in the source file at which the method is called. This is set at by the compiler.</param>
        public void Write(
            [CallerMemberName]string callerMemberName = null,
            [CallerFilePath]string callerFilePath = null,
            [CallerLineNumber]int callerLineNumber = 0) {
            if (callerMemberName != null)
                _data.MemberName = callerMemberName;
            if (callerFilePath != null)
                _data.FilePath = callerFilePath;
            if (callerLineNumber != 0)
                _data.LineNumber = callerLineNumber;

            _logger.Log(LogData.LogLevel, 0, LogData, LogData.Exception, _messageFormatter);
        }

        private static readonly Func<object, Exception, string> _messageFormatter = (state, error) => state.ToString();

        /// <summary>
        /// Writes the log event to the underlying logger if the condition delegate is true.
        /// </summary>
        /// <param name="condition">If condition is true, write log event; otherwise ignore event.</param>
        /// <param name="callerMemberName">The method or property name of the caller to the method. This is set at by the compiler.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is set at by the compiler.</param>
        /// <param name="callerLineNumber">The line number in the source file at which the method is called. This is set at by the compiler.</param>
        public void WriteIf(
            Func<bool> condition,
            [CallerMemberName]string callerMemberName = null,
            [CallerFilePath]string callerFilePath = null,
            [CallerLineNumber]int callerLineNumber = 0) {
            if (condition == null || !condition())
                return;

            Write(callerMemberName, callerFilePath, callerLineNumber);
        }

        /// <summary>
        /// Writes the log event to the underlying logger if the condition is true.
        /// </summary>
        /// <param name="condition">If condition is true, write log event; otherwise ignore event.</param>
        /// <param name="callerMemberName">The method or property name of the caller to the method. This is set at by the compiler.</param>
        /// <param name="callerFilePath">The full path of the source file that contains the caller. This is set at by the compiler.</param>
        /// <param name="callerLineNumber">The line number in the source file at which the method is called. This is set at by the compiler.</param>
        public void WriteIf(
            bool condition,
            [CallerMemberName]string callerMemberName = null,
            [CallerFilePath]string callerFilePath = null,
            [CallerLineNumber]int callerLineNumber = 0) {
            if (!condition)
                return;

            Write(callerMemberName, callerFilePath, callerLineNumber);
        }
    }
}