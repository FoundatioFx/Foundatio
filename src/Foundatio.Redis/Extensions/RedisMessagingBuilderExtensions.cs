using System;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="RedisMessageTransport"/> as the transport for Foundatio messaging.</summary>
public static class RedisMessagingBuilderExtensions
{
    /// <summary>
    /// Uses Redis Streams as the message transport, connecting with <paramref name="connectionString"/>. The connection
    /// is registered as the service collection's <see cref="IConnectionMultiplexer"/>, so jobs (<c>UseRedis</c> on the
    /// jobs builder with the same string), caches and locks can share it, and the service provider disposes it. Redis
    /// being down at startup does not fail it unless the string sets <c>abortConnect=true</c>.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="connectionString">The StackExchange.Redis configuration string, for example "localhost:6379".</param>
    /// <param name="options">
    /// The transport's options. A <see cref="RedisMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="RedisMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider: the clock is <see cref="MessagingOptions.TimeProvider"/> when that is set.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// An <see cref="IConnectionMultiplexer"/> is registered already, other than by a <c>UseRedis</c> call with the same connection string.
    /// </exception>
    public static FoundatioMessagingBuilder UseRedis(this FoundatioMessagingBuilder builder, string connectionString, RedisMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RedisConnectionRegistration.Add(builder.Services, connectionString);
        return builder.UseRedis(options);
    }

    /// <summary>
    /// Uses Redis Streams as the message transport, on the <see cref="IConnectionMultiplexer"/> registered in the
    /// service collection. The service provider creates and disposes the transport; the connection stays yours.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// The transport's options. A <see cref="RedisMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="RedisMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider: the clock is <see cref="MessagingOptions.TimeProvider"/> when that is set.
    /// </param>
    /// <exception cref="InvalidOperationException">No <see cref="IConnectionMultiplexer"/> is registered when the transport is created.</exception>
    public static FoundatioMessagingBuilder UseRedis(this FoundatioMessagingBuilder builder, RedisMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseTransport(sp => Create(sp, GetConnection(sp), options ?? new RedisMessageTransportOptions()));
    }

    /// <summary>
    /// Uses Redis Streams as the message transport, on the <see cref="IConnectionMultiplexer"/> registered in the
    /// service collection, with options built from the service provider, for example from configuration. The
    /// service provider creates and disposes the transport; the connection stays yours.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// Builds the transport's options. A <see cref="RedisMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="RedisMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider: the clock is <see cref="MessagingOptions.TimeProvider"/> when that is set.
    /// </param>
    /// <exception cref="InvalidOperationException">No <see cref="IConnectionMultiplexer"/> is registered when the transport is created.</exception>
    public static FoundatioMessagingBuilder UseRedis(this FoundatioMessagingBuilder builder, Func<IServiceProvider, RedisMessageTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, GetConnection(sp), options(sp)));
    }

    /// <summary>
    /// Uses Redis Streams as the message transport, on <paramref name="connection"/>. The service provider creates
    /// and disposes the transport; the connection stays yours.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="connection">The connection to use.</param>
    /// <param name="options">
    /// The transport's options. A <see cref="RedisMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="RedisMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider: the clock is <see cref="MessagingOptions.TimeProvider"/> when that is set.
    /// </param>
    public static FoundatioMessagingBuilder UseRedis(this FoundatioMessagingBuilder builder, IConnectionMultiplexer connection, RedisMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);
        return builder.UseTransport(sp => Create(sp, connection, options ?? new RedisMessageTransportOptions()));
    }

    private static RedisMessageTransport Create(IServiceProvider services, IConnectionMultiplexer connection, RedisMessageTransportOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseRedis returned null.");

        return new RedisMessageTransport(connection, options with
        {
            LoggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>(),
            TimeProvider = options.TimeProvider ?? services.GetService<IOptions<MessagingOptions>>()?.Value.TimeProvider ?? services.GetService<TimeProvider>()
        });
    }

    private static IConnectionMultiplexer GetConnection(IServiceProvider services)
    {
        return services.GetService<IConnectionMultiplexer>()
            ?? throw new InvalidOperationException("UseRedis() inside AddFoundatioMessaging needs an IConnectionMultiplexer. Register one, for example services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(\"localhost\")), or pass one to UseRedis(connection).");
    }
}
