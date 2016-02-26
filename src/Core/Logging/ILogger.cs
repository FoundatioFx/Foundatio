using System;

namespace Foundatio.Logging {
    public interface ILogger {
        void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter);
        bool IsEnabled(LogLevel logLevel);
        IDisposable BeginScope<TState, TScope>(Func<TState, TScope> scopeFactory, TState state);
    }

    public interface ILogger<T> : ILogger {}
}
