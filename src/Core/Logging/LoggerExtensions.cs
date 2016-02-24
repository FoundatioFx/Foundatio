using System;
using System.Collections.Generic;
using Foundatio.Logging.Internal;

namespace Foundatio.Logging {
    public static class LoggerExtensions {
        private static readonly Func<object, Exception, string> _messageFormatter = MessageFormatter;

        //------------------------------------------DEBUG------------------------------------------//

        /// <summary>
        /// Formats and writes a debug log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Debug(this ILogger logger, EventId eventId, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Debug, eventId, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a debug log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Debug(this ILogger logger, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Debug, 0, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a debug log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Debug(this ILogger logger, EventId eventId, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Debug, eventId, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a debug log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Debug(this ILogger logger, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Debug, 0, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        //------------------------------------------TRACE------------------------------------------//

        /// <summary>
        /// Formats and writes a trace log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Trace(this ILogger logger, EventId eventId, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Trace, eventId, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a trace log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Trace(this ILogger logger, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Trace, 0, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a trace log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Trace(this ILogger logger, EventId eventId, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Trace, eventId, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a trace log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Trace(this ILogger logger, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Trace, 0, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        //------------------------------------------INFORMATION------------------------------------------//

        /// <summary>
        /// Formats and writes an informational log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Info(this ILogger logger, EventId eventId, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Information, eventId, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes an informational log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Info(this ILogger logger, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Information, 0, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes an informational log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Info(this ILogger logger, EventId eventId, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Information, eventId, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes an informational log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Info(this ILogger logger, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Information, 0, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        //------------------------------------------WARNING------------------------------------------//

        /// <summary>
        /// Formats and writes a warning log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Warn(this ILogger logger, EventId eventId, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Warning, eventId, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a warning log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Warn(this ILogger logger, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Warning, 0, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a warning log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Warn(this ILogger logger, EventId eventId, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Warning, eventId, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a warning log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Warn(this ILogger logger, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Warning, 0, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        //------------------------------------------ERROR------------------------------------------//

        /// <summary>
        /// Formats and writes an error log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Error(this ILogger logger, EventId eventId, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Error, eventId, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes an error log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Error(this ILogger logger, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Error, 0, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes an error log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Error(this ILogger logger, EventId eventId, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Error, eventId, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes an error log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Error(this ILogger logger, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Error, 0, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        //------------------------------------------CRITICAL------------------------------------------//

        /// <summary>
        /// Formats and writes a critical log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Critical(this ILogger logger, EventId eventId, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Critical, eventId, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a critical log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Critical(this ILogger logger, Exception exception, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Critical, 0, new FormattedLogValues(message, args), exception, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a critical log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="eventId">The event id associated with the log.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Critical(this ILogger logger, EventId eventId, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Critical, eventId, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        /// <summary>
        /// Formats and writes a critical log message.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to write to.</param>
        /// <param name="message">Format string of the log message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        public static void Critical(this ILogger logger, string message, params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            logger.Log(LogLevel.Critical, 0, new FormattedLogValues(message, args), null, _messageFormatter);
        }

        //------------------------------------------Scope------------------------------------------//

        /// <summary>
        /// Formats the message and creates a scope.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to create the scope in.</param>
        /// <param name="messageFormat">Format string of the scope message.</param>
        /// <param name="args">An object array that contains zero or more objects to format.</param>
        /// <returns>A disposable scope object. Can be null.</returns>
        public static IDisposable BeginScope(
            this ILogger logger,
            string messageFormat,
            params object[] args) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            if (messageFormat == null) {
                throw new ArgumentNullException(nameof(messageFormat));
            }

            return logger.BeginScope(s => s.ToString(), new FormattedLogValues(messageFormat, args));
        }

        /// <summary>
        /// Creates a scoped log property.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger"/> to create the scope in.</param>
        /// <param name="name">The name of the scope property.</param>
        /// <param name="value">The value of the scope property.</param>
        /// <returns>A disposable scope object. Can be null.</returns>
        public static IDisposable BeginPropertyScope(
            this ILogger logger,
            string name,
            string value) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }

            return logger.BeginScope(s => s, new KeyValuePair<string, string>(name, value));
        }

        //------------------------------------------HELPERS------------------------------------------//

        private static string MessageFormatter(object state, Exception error) {
            return state.ToString();
        }

        //------------------------------------------BUILDERS------------------------------------------//

        public static ILogBuilder Trace(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Trace))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Trace, logger);
        }

        public static ILogBuilder Debug(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Debug))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Debug, logger);
        }

        public static ILogBuilder Info(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Information))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Information, logger);
        }

        public static ILogBuilder Warn(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Warning))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Warning, logger);
        }

        public static ILogBuilder Error(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Error))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Error, logger);
        }

        public static ILogBuilder Critical(this ILogger logger) {
            if (!logger.IsEnabled(LogLevel.Critical))
                return NullLogBuilder.Instance;

            return new LogBuilder(LogLevel.Critical, logger);
        }
    }
}