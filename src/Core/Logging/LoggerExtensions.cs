using System;
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

        public static void Debug(this ILogger logger, Exception exception, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Debug, 0, null, exception, (state, ex) => formatter());
        }

        public static void Debug(this ILogger logger, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Debug, 0, null, null, (state, exception) => formatter());
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

        public static void Trace(this ILogger logger, Exception exception, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Trace, 0, null, exception, (state, ex) => formatter());
        }

        public static void Trace(this ILogger logger, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Trace, 0, null, null, (state, exception) => formatter());
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

        public static void Info(this ILogger logger, Exception exception, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Information, 0, null, exception, (state, ex) => formatter());
        }

        public static void Info(this ILogger logger, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Information, 0, null, null, (state, exception) => formatter());
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

        public static void Warn(this ILogger logger, Exception exception, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Warning, 0, null, exception, (state, ex) => formatter());
        }

        public static void Warn(this ILogger logger, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Warning, 0, null, null, (state, exception) => formatter());
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

        public static void Error(this ILogger logger, Exception exception, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Error, 0, null, exception, (state, ex) => formatter());
        }

        public static void Error(this ILogger logger, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Error, 0, null, null, (state, exception) => formatter());
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

        public static void Critical(this ILogger logger, Exception exception, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Critical, 0, null, exception, (state, ex) => formatter());
        }

        public static void Critical(this ILogger logger, Func<string> formatter) {
            if (logger == null) {
                throw new ArgumentNullException(nameof(logger));
            }
            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            logger.Log<object>(LogLevel.Critical, 0, null, null, (state, exception) => formatter());
        }

        public static ILogger GetLogger(this object target) {
            var accessor = target as IHaveLogger;
            return accessor != null ? accessor.Logger : NullLogger.Instance;
        }

        //------------------------------------------HELPERS------------------------------------------//

        private static string MessageFormatter(object state, Exception error) {
            return state.ToString();
        }
    }
}