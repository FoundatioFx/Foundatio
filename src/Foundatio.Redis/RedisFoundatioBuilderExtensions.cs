using System;
using System.Linq;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Foundatio;

public static class RedisFoundatioBuilderExtensions
{
    /// <summary>
    /// Backs the durable job runtime with Redis. Uses an <see cref="IConnectionMultiplexer"/> already registered in DI,
    /// otherwise connects using <paramref name="connectionString"/> or the "Redis" connection string from configuration
    /// (falling back to localhost). When both messaging and jobs use Redis a single connection is shared, so the
    /// explicit connection settings must agree. Conflicting settings fail during registration.
    /// </summary>
    public static FoundatioBuilder UseRedis(this FoundatioBuilder.JobsBuilder builder, Action<RedisJobRuntimeStoreOptions>? configure = null, string? connectionString = null)
    {
        EnsureConnection(((IFoundatioBuilder)builder).Services, connectionString);
        return builder.UseRuntimeStore(sp =>
        {
            var options = new RedisJobRuntimeStoreOptions { ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>() };
            configure?.Invoke(options);
            return new RedisJobRuntimeStore(options);
        });
    }

    /// <summary>
    /// Runs messaging (queues + pub/sub) over Redis Streams. Uses an <see cref="IConnectionMultiplexer"/> already
    /// registered in DI, otherwise connects using <paramref name="connectionString"/> or the "Redis" connection string
    /// from configuration (falling back to localhost). When both messaging and jobs use Redis a single connection is
    /// shared. Explicit connection settings must agree; conflicting settings fail during registration.
    /// </summary>
    public static FoundatioBuilder UseRedis(this FoundatioBuilder.MessagingBuilder builder, Action<RedisStreamsMessageTransportOptions>? configure = null, string? connectionString = null)
    {
        EnsureConnection(((IFoundatioBuilder)builder).Services, connectionString);
        return builder.UseTransport(sp =>
        {
            var options = new RedisStreamsMessageTransportOptions { ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>() };
            configure?.Invoke(options);
            return new RedisStreamsMessageTransport(options);
        });
    }

    private static void EnsureConnection(IServiceCollection services, string? connectionString)
    {
        var settings = services.FirstOrDefault(d => d.ServiceType == typeof(RedisConnectionSettings))?.ImplementationInstance as RedisConnectionSettings;
        if (settings is not null)
        {
            if (connectionString is not null && settings.ConnectionString is not null && !String.Equals(connectionString, settings.ConnectionString, StringComparison.Ordinal))
                throw new ArgumentException("Messaging and jobs share one Redis connection. Supply the same connection string, or configure it once and omit it on subsequent UseRedis calls.", nameof(connectionString));
            settings.ConnectionString ??= connectionString;
            return;
        }

        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            if (connectionString is not null)
                throw new ArgumentException("A Redis connection is already registered. Omit connectionString from UseRedis to use that connection.", nameof(connectionString));
            return;
        }

        settings = new RedisConnectionSettings { ConnectionString = connectionString };
        services.AddSingleton(settings);
        services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(
            settings.ConnectionString
            ?? sp.GetService<IConfiguration>()?.GetConnectionString("Redis")
            ?? "localhost:6379"));
    }

    private sealed class RedisConnectionSettings
    {
        public string? ConnectionString { get; set; }
    }
}
