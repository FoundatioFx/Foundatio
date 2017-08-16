using System;
using System.Collections.Generic;
using System.Threading;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace Foundatio.Logging.Serilog
{
    /// <summary>
    /// An <see cref="ILoggerProvider"/> that pipes events through Serilog.
    /// </summary>
    public class SerilogLoggerProvider : ILoggerProvider, ILogEventEnricher {
        internal const string OriginalFormatPropertyName = "{OriginalFormat}";
        internal const string ScopePropertyName = "Scope";

        // May be null; if it is, Log.Logger will be lazily used
        readonly global::Serilog.ILogger _logger;
        readonly Action _dispose;

        /// <summary>
        /// Construct a <see cref="SerilogLoggerProvider"/>.
        /// </summary>
        /// <param name="logger">A Serilog logger to pipe events through; if null, the static <see cref="Log"/> class will be used.</param>
        /// <param name="dispose">If true, the provided logger or static log class will be disposed/closed when the provider is disposed.</param>
        public SerilogLoggerProvider(global::Serilog.ILogger logger = null, bool dispose = false) {
            if (logger != null)
                _logger = logger.ForContext(new[] { this });

            if (dispose) {
                if (logger != null)
                    _dispose = () => (logger as IDisposable)?.Dispose();
                else
                    _dispose = Log.CloseAndFlush;
            }
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string name) {
            return new SerilogLogger(this, _logger, name);
        }

        /// <inheritdoc />
        public IDisposable BeginScope<T>(T state) {
            if (CurrentScope != null)
                return new SerilogLoggerScope(this, state);

            // The outermost scope pushes and pops the Serilog `LogContext` - once
            // this enricher is on the stack, the `CurrentScope` property takes care
            // of the rest of the `BeginScope()` stack.
            var popSerilogContext = LogContext.Push(this);
            return new SerilogLoggerScope(this, state, popSerilogContext);
        }

        /// <inheritdoc />
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) {
            List<LogEventPropertyValue> scopeItems = null;
            for (var scope = CurrentScope; scope != null; scope = scope.Parent) {
                scope.EnrichAndCreateScopeItem(logEvent, propertyFactory, out var scopeItem);

                if (scopeItem != null) {
                    scopeItems = scopeItems ?? new List<LogEventPropertyValue>();
                    scopeItems.Add(scopeItem);
                }
            }

            if (scopeItems != null) {
                scopeItems.Reverse();
                logEvent.AddPropertyIfAbsent(new LogEventProperty(ScopePropertyName, new SequenceValue(scopeItems)));
            }
        }

        readonly AsyncLocal<SerilogLoggerScope> _value = new AsyncLocal<SerilogLoggerScope>();

        internal SerilogLoggerScope CurrentScope
        {
            get => _value.Value;
            set => _value.Value = value;
        }

        /// <inheritdoc />
        public void Dispose() {
            _dispose?.Invoke();
        }
    }
}