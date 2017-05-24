using System;
using System.Collections.Generic;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Foundatio.Logging.Serilog
{
    class SerilogLogger : ILogger {
        readonly SerilogLoggerProvider _provider;
        readonly global::Serilog.ILogger _logger;

        static readonly MessageTemplateParser _messageTemplateParser = new MessageTemplateParser();

        public SerilogLogger(SerilogLoggerProvider provider, global::Serilog.ILogger logger = null, string name = null) {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            _provider = provider;
            _logger = logger;

            // If a logger was passed, the provider has already added itself as an enricher
            _logger = _logger ?? global::Serilog.Log.Logger.ForContext(new[] { provider });

            if (name != null) {
                _logger = _logger.ForContext(Constants.SourceContextPropertyName, name);
            }
        }

        public bool IsEnabled(LogLevel logLevel) {
            return _logger.IsEnabled(ConvertLevel(logLevel));
        }

        public IDisposable BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state) {
            return _provider.BeginScope(state);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            var level = ConvertLevel(logLevel);
            if (!_logger.IsEnabled(level)) {
                return;
            }

            var logger = _logger;
            string messageTemplate = null;

            var properties = new List<LogEventProperty>();

            var structure = state as IEnumerable<KeyValuePair<string, object>>;
            if (structure != null) {
                foreach (var property in structure) {
                    if (property.Key == SerilogLoggerProvider.OriginalFormatPropertyName && property.Value is string) {
                        messageTemplate = (string)property.Value;
                    }
                    else if (property.Key.StartsWith("@")) {
                        LogEventProperty destructed;
                        if (logger.BindProperty(property.Key.Substring(1), property.Value, true, out destructed))
                            properties.Add(destructed);
                    }
                    else {
                        LogEventProperty bound;
                        if (logger.BindProperty(property.Key, property.Value, false, out bound))
                            properties.Add(bound);
                    }
                }

                var stateType = state.GetType();
                var stateTypeInfo = stateType.GetTypeInfo();
                // Imperfect, but at least eliminates `1 names
                if (messageTemplate == null && !stateTypeInfo.IsGenericType) {
                    messageTemplate = "{" + stateType.Name + ":l}";
                    LogEventProperty stateTypeProperty;
                    if (logger.BindProperty(stateType.Name, AsLoggableValue(state, formatter), false, out stateTypeProperty))
                        properties.Add(stateTypeProperty);
                }
            }

            if (messageTemplate == null) {
                string propertyName = null;
                if (state != null) {
                    propertyName = "State";
                    messageTemplate = "{State:l}";
                }
                else if (formatter != null) {
                    propertyName = "Message";
                    messageTemplate = "{Message:l}";
                }

                if (propertyName != null) {
                    LogEventProperty property;
                    if (logger.BindProperty(propertyName, AsLoggableValue(state, formatter), false, out property))
                        properties.Add(property);
                }
            }

            if (eventId.Id != 0 || eventId.Name != null)
                properties.Add(CreateEventIdProperty(eventId));

            var parsedTemplate = _messageTemplateParser.Parse(messageTemplate ?? "");
            var evt = new LogEvent(DateTimeOffset.Now, level, exception, parsedTemplate, properties);
            logger.Write(evt);
        }

        static object AsLoggableValue<TState>(TState state, Func<TState, Exception, string> formatter) {
            object sobj = state;
            if (formatter != null)
                sobj = formatter(state, null);
            return sobj;
        }

        static LogEventLevel ConvertLevel(LogLevel logLevel) {
            switch (logLevel) {
                case LogLevel.Critical:
                    return LogEventLevel.Fatal;
                case LogLevel.Error:
                    return LogEventLevel.Error;
                case LogLevel.Warning:
                    return LogEventLevel.Warning;
                case LogLevel.Information:
                    return LogEventLevel.Information;
                case LogLevel.Debug:
                    return LogEventLevel.Debug;
                // ReSharper disable once RedundantCaseLabel
                case LogLevel.Trace:
                default:
                    return LogEventLevel.Verbose;
            }
        }

        static LogEventProperty CreateEventIdProperty(EventId eventId) {
            var properties = new List<LogEventProperty>(2);

            if (eventId.Id != 0) {
                properties.Add(new LogEventProperty("Id", new ScalarValue(eventId.Id)));
            }

            if (eventId.Name != null) {
                properties.Add(new LogEventProperty("Name", new ScalarValue(eventId.Name)));
            }

            return new LogEventProperty("EventId", new StructureValue(properties));
        }
    }
}