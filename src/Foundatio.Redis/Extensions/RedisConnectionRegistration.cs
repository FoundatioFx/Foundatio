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
    /// string sets <c>abortConnect</c>.
    /// </summary>
    private static ConnectionMultiplexer Connect(string connectionString, ILoggerFactory? loggerFactory)
    {
        var configuration = ConfigurationOptions.Parse(connectionString);
        if (!connectionString.Contains("abortConnect", StringComparison.OrdinalIgnoreCase))
            configuration.AbortOnConnectFail = false;
        configuration.LoggerFactory ??= loggerFactory;
        return ConnectionMultiplexer.Connect(configuration);
    }

    /// <summary>Marks the connection string the registered connection was made for. Never printed, as it may hold a password.</summary>
    private sealed class Registered(string connectionString)
    {
        public string ConnectionString { get; } = connectionString;
    }
}
