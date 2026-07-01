using System;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Foundatio;

public static class RedisFoundatioBuilderExtensions
{
    /// <summary>
    /// Backs the durable job runtime with Redis. Uses an <see cref="IConnectionMultiplexer"/> already registered in DI,
    /// otherwise connects using <paramref name="connectionString"/> or the "Redis" connection string from configuration
    /// (falling back to localhost). The connection is shared with <see cref="UseRedis(FoundatioBuilder.MessagingBuilder, Action{RedisStreamsMessageTransportOptions}, string)"/>.
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
    /// from configuration (falling back to localhost).
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

    // Register a single shared multiplexer if the app hasn't already, so messaging and jobs reuse one connection.
    private static void EnsureConnection(IServiceCollection services, string? connectionString)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(
            connectionString
            ?? sp.GetService<IConfiguration>()?.GetConnectionString("Redis")
            ?? "localhost:6379"));
    }
}
