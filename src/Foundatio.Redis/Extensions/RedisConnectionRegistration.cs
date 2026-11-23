using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the <see cref="IConnectionMultiplexer"/> a <c>UseRedis(connectionString)</c> call connects with, once,
/// so messaging, jobs and anything else resolving it from the service provider share one connection, which the
/// service provider disposes.
/// </summary>
internal static class RedisConnectionRegistration
{
    public static void Add(IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (services.LastOrDefault(d => d.ServiceType == typeof(Registered))?.ImplementationInstance is Registered registered)
        {
            if (!String.Equals(registered.ConnectionString, connectionString, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("UseRedis(connectionString) was given another connection string than an earlier UseRedis(connectionString) call, " +
                    "which registered the IConnectionMultiplexer both would use. Use one connection string, or pass each connection to UseRedis(connection).");
            }

            return;
        }

        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            throw new InvalidOperationException("UseRedis(connectionString) registers an IConnectionMultiplexer, but one is registered already. " +
                "Call UseRedis() to use the registered one, or UseRedis(connection) to pass another.");
        }

        services.AddSingleton(new Registered(connectionString));
        services.AddSingleton<IConnectionMultiplexer>(sp => Connect(connectionString, sp.GetService<ILoggerFactory>()));
    }

    /// <summary>
    /// Connects without failing when Redis is down at startup (it reconnects in the background), unless the connection
    /// string sets <c>abortConnect</c>. The connection logs only its warnings and errors: StackExchange.Redis logs about
    /// a hundred Information lines for each connect and disconnect, including a <c>SocketClosed</c> exception for each
    /// socket on a normal shutdown. To see them, register an <see cref="IConnectionMultiplexer"/> whose
    /// <see cref="ConfigurationOptions.LoggerFactory"/> is set and call <c>UseRedis()</c>.
    /// </summary>
    private static ConnectionMultiplexer Connect(string connectionString, ILoggerFactory? loggerFactory)
    {
        var configuration = ConfigurationOptions.Parse(connectionString);
        if (!connectionString.Contains("abortConnect", StringComparison.OrdinalIgnoreCase))
            configuration.AbortOnConnectFail = false;
        if (loggerFactory is not null)
            configuration.LoggerFactory ??= new WarningLoggerFactory(loggerFactory);
        return ConnectionMultiplexer.Connect(configuration);
    }

    /// <summary>Passes on only the log entries at <see cref="LogLevel.Warning"/> and above.</summary>
    private sealed class WarningLoggerFactory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new WarningLogger(inner.CreateLogger(categoryName));

        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

        public void Dispose() { }

        private sealed class WarningLogger(ILogger inner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && inner.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }

    /// <summary>Marks the connection string the registered connection was made for. Never printed, as it may hold a password.</summary>
    private sealed class Registered(string connectionString)
    {
        public string ConnectionString { get; } = connectionString;
    }
}
