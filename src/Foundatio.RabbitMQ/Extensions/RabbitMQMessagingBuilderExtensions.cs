using System;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="RabbitMQMessageTransport"/> as the transport for Foundatio messaging.</summary>
public static class RabbitMQMessagingBuilderExtensions
{
    /// <summary>
    /// Uses RabbitMQ as the message transport, connecting to <paramref name="connectionString"/> with the default
    /// options. The service provider creates and disposes the transport and its connections.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="connectionString">An <c>amqp://</c> or <c>amqps://</c> URI, for example "amqp://guest:guest@localhost:5672".</param>
    public static FoundatioMessagingBuilder UseRabbitMQ(this FoundatioMessagingBuilder builder, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseRabbitMQ(new RabbitMQMessageTransportOptions { ConnectionString = connectionString });
    }

    /// <summary>
    /// Uses RabbitMQ as the message transport. With a <see cref="RabbitMQMessageTransportOptions.ConnectionString"/>,
    /// the transport opens its own connections; without one, it uses the <see cref="IConnection"/> registered in the
    /// service collection (for example by Aspire's RabbitMQ client integration), which stays the app's. The service
    /// provider creates and disposes the transport.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// The transport's options. A <see cref="RabbitMQMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="RabbitMQMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider.
    /// </param>
    /// <exception cref="InvalidOperationException">There is no connection string and no <see cref="IConnection"/> is registered when the transport is created.</exception>
    public static FoundatioMessagingBuilder UseRabbitMQ(this FoundatioMessagingBuilder builder, RabbitMQMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseTransport(sp => Create(sp, null, options ?? new RabbitMQMessageTransportOptions()));
    }

    /// <summary>
    /// Uses RabbitMQ as the message transport, with options built from the service provider, for example from
    /// configuration. With a <see cref="RabbitMQMessageTransportOptions.ConnectionString"/>, the transport opens its own
    /// connections; without one, it uses the <see cref="IConnection"/> registered in the service collection, which
    /// stays the app's. The service provider creates and disposes the transport.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// Builds the transport's options. A <see cref="RabbitMQMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="RabbitMQMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider.
    /// </param>
    /// <exception cref="InvalidOperationException">There is no connection string and no <see cref="IConnection"/> is registered when the transport is created.</exception>
    public static FoundatioMessagingBuilder UseRabbitMQ(this FoundatioMessagingBuilder builder, Func<IServiceProvider, RabbitMQMessageTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, null, options(sp)));
    }

    /// <summary>
    /// Uses RabbitMQ as the message transport, on <paramref name="connection"/>. The service provider creates and
    /// disposes the transport; the connection stays yours.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="connection">The connection to publish and receive on.</param>
    /// <param name="options">
    /// The transport's options; its <see cref="RabbitMQMessageTransportOptions.ConnectionString"/> is only used for the
    /// virtual host and credentials of <see cref="RabbitMQMessageTransportOptions.ManagementEndpoint"/>. A
    /// <see cref="RabbitMQMessageTransportOptions.LoggerFactory"/> or <see cref="RabbitMQMessageTransportOptions.TimeProvider"/>
    /// left unset is taken from the service provider.
    /// </param>
    public static FoundatioMessagingBuilder UseRabbitMQ(this FoundatioMessagingBuilder builder, IConnection connection, RabbitMQMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);
        return builder.UseTransport(sp => Create(sp, connection, options ?? new RabbitMQMessageTransportOptions()));
    }

    private static RabbitMQMessageTransport Create(IServiceProvider services, IConnection? connection, RabbitMQMessageTransportOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseRabbitMQ returned null.");

        options = options with
        {
            LoggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>(),
            TimeProvider = options.TimeProvider ?? services.GetService<TimeProvider>()
        };

        if (connection is not null)
            return new RabbitMQMessageTransport(connection, options);
        if (!String.IsNullOrWhiteSpace(options.ConnectionString))
            return new RabbitMQMessageTransport(options);

        return new RabbitMQMessageTransport(services.GetService<IConnection>()
            ?? throw new InvalidOperationException("UseRabbitMQ() inside AddFoundatioMessaging needs a connection string or an IConnection. Pass one, " +
                "for example UseRabbitMQ(\"amqp://guest:guest@localhost:5672\"), or register an IConnection, for example with Aspire's AddRabbitMQClient."), options);
    }
}
