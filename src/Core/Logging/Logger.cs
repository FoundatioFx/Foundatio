using System;
using System.Collections.Generic;
using Foundatio.Utility;

namespace Foundatio.Logging {
    internal class Logger : ILogger {
        private static readonly NullScope _nullScope = new NullScope();

        private readonly LoggerFactory _loggerFactory;
        private readonly string _categoryName;
        private LogLevel _minLogLevel;
        private ILogger[] _loggers;

        public Logger(LoggerFactory loggerFactory, string categoryName, LogLevel minLogLevel) {
            _loggerFactory = loggerFactory;
            _categoryName = categoryName;
            _minLogLevel = minLogLevel;

            var providers = loggerFactory.GetProviders();
            if (providers.Length <= 0)
                return;

            _loggers = new ILogger[providers.Length];
            for (var index = 0; index < providers.Length; index++)
                _loggers[index] = providers[index].CreateLogger(categoryName);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            if (_loggers == null || logLevel < _minLogLevel)
                return;

            List<Exception> exceptions = null;
            foreach (var logger in _loggers) {
                try {
                    logger.Log(logLevel, eventId, state, exception, formatter);
                } catch (Exception ex) {
                    if (exceptions == null)
                        exceptions = new List<Exception>();

                    exceptions.Add(ex);
                }
            }

            if (exceptions != null && exceptions.Count > 0)
                throw new AggregateException("An error occurred while writing to logger(s).", exceptions);
        }

        public bool IsEnabled(LogLevel logLevel) {
            if (_loggers == null)
                return false;

            List<Exception> exceptions = null;
            foreach (var logger in _loggers) {
                try {
                    if (logger.IsEnabled(logLevel))
                        return true;
                } catch (Exception ex) {
                    if (exceptions == null)
                        exceptions = new List<Exception>();

                    exceptions.Add(ex);
                }
            }

            if (exceptions != null && exceptions.Count > 0)
                throw new AggregateException("An error occurred while writing to logger(s).", exceptions);

            return false;
        }

        public IDisposable BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state) {
            if (_loggers == null)
                return _nullScope;

            if (_loggers.Length == 1)
                return _loggers[0].BeginScope(scopeFactory, state);

            var loggers = _loggers;

            var scope = new Scope(loggers.Length);
            List<Exception> exceptions = null;
            for (var index = 0; index < loggers.Length; index++) {
                try {
                    var disposable = loggers[index].BeginScope(scopeFactory, state);
                    scope.SetDisposable(index, disposable);
                } catch (Exception ex) {
                    if (exceptions == null)
                        exceptions = new List<Exception>();

                    exceptions.Add(ex);
                }
            }

            if (exceptions != null && exceptions.Count > 0)
                throw new AggregateException("An error occurred while writing to logger(s).", exceptions);

            return scope;
        }

        internal void AddProvider(ILoggerProvider provider) {
            var logger = provider.CreateLogger(_categoryName);

            int logIndex;
            if (_loggers == null) {
                logIndex = 0;
                _loggers = new ILogger[1];
            } else {
                logIndex = _loggers.Length;
                Array.Resize(ref _loggers, logIndex + 1);
            }

            _loggers[logIndex] = logger;
        }

        internal void ChangeMinLogLevel(LogLevel minLogLevel) {
            _minLogLevel = minLogLevel;
        }

        private class Scope : IDisposable {
            private bool _isDisposed;

            private IDisposable _disposable0;
            private IDisposable _disposable1;
            private readonly IDisposable[] _disposable;

            public Scope(int count) {
                if (count > 2)
                    _disposable = new IDisposable[count - 2];
            }

            public void SetDisposable(int index, IDisposable disposable) {
                if (index == 0)
                    _disposable0 = disposable;
                else if (index == 1)
                    _disposable1 = disposable;
                else
                    _disposable[index - 2] = disposable;
            }

            public void Dispose() {
                if (_isDisposed)
                    return;

                _disposable0?.Dispose();
                _disposable1?.Dispose();

                if (_disposable != null) {
                    var count = _disposable.Length;
                    for (var index = 0; index != count; ++index) {
                        if (_disposable[index] != null)
                            _disposable[index].Dispose();
                    }
                }

                _isDisposed = true;
            }

            internal void Add(IDisposable disposable) {
                throw new NotImplementedException();
            }
        }

        private class NullScope : IDisposable {
            public void Dispose() {}
        }
    }

    public class Logger<T> : ILogger<T> {
        private readonly ILogger _logger;

        public Logger(ILoggerFactory factory) {
            _logger = factory != null ? factory.CreateLogger(TypeHelper.GetTypeDisplayName(typeof(T))) : NullLogger.Instance;
        }

        IDisposable ILogger.BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state) {
            return _logger.BeginScope(scopeFactory, state);
        }

        bool ILogger.IsEnabled(LogLevel logLevel) {
            return _logger.IsEnabled(logLevel);
        }

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
            _logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
