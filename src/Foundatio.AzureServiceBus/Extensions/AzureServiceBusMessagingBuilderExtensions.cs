using System;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="AzureServiceBusMessageTransport"/> as the transport for Foundatio messaging.</summary>
public static class AzureServiceBusMessagingBuilderExtensions
{
    /// <summary>
    /// Uses Azure Service Bus as the message transport, connecting with <paramref name="connectionString"/> and the
    /// default options. The service provider creates and disposes the transport and its clients.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="connectionString">The namespace's connection string, for example "Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...".</param>
    public static FoundatioMessagingBuilder UseAzureServiceBus(this FoundatioMessagingBuilder builder, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseAzureServiceBus(new AzureServiceBusMessageTransportOptions { ConnectionString = connectionString });
    }

    /// <summary>
    /// Uses Azure Service Bus as the message transport. With a connection string or namespace in
    /// <paramref name="options"/>, the service provider creates and disposes the transport and its clients. Without
    /// one, the transport uses the <see cref="ServiceBusClient"/> and <see cref="ServiceBusAdministrationClient"/>
    /// registered in the service collection, which stay yours.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// The transport's options. A <see cref="AzureServiceBusMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="AzureServiceBusMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The options have no connection and no <see cref="ServiceBusClient"/> is registered when the transport is created,
    /// or no <see cref="ServiceBusAdministrationClient"/> is registered while <see cref="AzureServiceBusMessageTransportOptions.CreateEntities"/> is on.
    /// </exception>
    public static FoundatioMessagingBuilder UseAzureServiceBus(this FoundatioMessagingBuilder builder, AzureServiceBusMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseTransport(sp => Create(sp, options ?? new AzureServiceBusMessageTransportOptions()));
    }

    /// <summary>
    /// Uses Azure Service Bus as the message transport, with options built from the service provider, for example from
    /// configuration. With a connection string or namespace in the options, the service provider creates and disposes
    /// the transport and its clients; without one, the transport uses the clients registered in the service collection.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// Builds the transport's options. A <see cref="AzureServiceBusMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="AzureServiceBusMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider.
    /// </param>
    public static FoundatioMessagingBuilder UseAzureServiceBus(this FoundatioMessagingBuilder builder, Func<IServiceProvider, AzureServiceBusMessageTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, options(sp)));
    }

    /// <summary>
    /// Uses Azure Service Bus as the message transport, on <paramref name="client"/>. The service provider creates and
    /// disposes the transport; the clients stay yours.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="client">The client to send and receive with.</param>
    /// <param name="administrationClient">
    /// Creates, counts and deletes entities. It may be null only with <see cref="AzureServiceBusMessageTransportOptions.CreateEntities"/> off.
    /// </param>
    /// <param name="options">
    /// The transport's options; its connection settings are not used. A <see cref="AzureServiceBusMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="AzureServiceBusMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider.
    /// </param>
    public static FoundatioMessagingBuilder UseAzureServiceBus(this FoundatioMessagingBuilder builder, ServiceBusClient client, ServiceBusAdministrationClient? administrationClient,
        AzureServiceBusMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);
        return builder.UseTransport(sp => new AzureServiceBusMessageTransport(client, administrationClient, WithServices(sp, options ?? new AzureServiceBusMessageTransportOptions())));
    }

    private static AzureServiceBusMessageTransport Create(IServiceProvider services, AzureServiceBusMessageTransportOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseAzureServiceBus returned null.");

        options = WithServices(services, options);
        if (!String.IsNullOrEmpty(options.ConnectionString) || !String.IsNullOrEmpty(options.FullyQualifiedNamespace))
            return new AzureServiceBusMessageTransport(options);

        var client = services.GetService<ServiceBusClient>()
            ?? throw new InvalidOperationException("UseAzureServiceBus() inside AddFoundatioMessaging needs a connection: set ConnectionString or FullyQualifiedNamespace in its options, " +
                "pass one to UseAzureServiceBus(connectionString), or register a ServiceBusClient (and a ServiceBusAdministrationClient) in the service collection.");

        var administrationClient = services.GetService<ServiceBusAdministrationClient>();
        if (administrationClient is null && options.CreateEntities)
        {
            throw new InvalidOperationException("UseAzureServiceBus() found a ServiceBusClient but no ServiceBusAdministrationClient in the service collection, which it needs to create " +
                "queues, topics and subscriptions. Register one, or set CreateEntities = false when the entities are created ahead of time.");
        }

        return new AzureServiceBusMessageTransport(client, administrationClient, options);
    }

    private static AzureServiceBusMessageTransportOptions WithServices(IServiceProvider services, AzureServiceBusMessageTransportOptions options) => options with
    {
        LoggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>(),
        TimeProvider = options.TimeProvider ?? services.GetService<TimeProvider>()
    };
}
