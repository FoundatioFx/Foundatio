using System;
using Azure.Storage.Queues;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="AzureStorageQueueMessageTransport"/> as the messaging transport.</summary>
public static class AzureStorageQueueMessagingBuilderExtensions
{
    /// <summary>Uses Azure Storage Queues through the storage account's connection string, for example <c>UseDevelopmentStorage=true</c> for Azurite.</summary>
    public static FoundatioMessagingBuilder UseAzureStorageQueues(this FoundatioMessagingBuilder builder, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseAzureStorageQueues(new AzureStorageQueueMessageTransportOptions { ConnectionString = connectionString });
    }

    /// <summary>
    /// Uses Azure Storage Queues with <paramref name="options"/>. Without a connection string or service URI in them,
    /// uses the <see cref="QueueServiceClient"/> registered in the service collection.
    /// </summary>
    public static FoundatioMessagingBuilder UseAzureStorageQueues(this FoundatioMessagingBuilder builder, AzureStorageQueueMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseTransport(sp => Create(sp, options ?? new AzureStorageQueueMessageTransportOptions()));
    }

    /// <summary>Uses Azure Storage Queues with options built from the service provider, for example from configuration.</summary>
    public static FoundatioMessagingBuilder UseAzureStorageQueues(this FoundatioMessagingBuilder builder, Func<IServiceProvider, AzureStorageQueueMessageTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, options(sp)));
    }

    /// <summary>
    /// Uses Azure Storage Queues through <paramref name="service"/>, which the app owns. Its clients must not
    /// base64-encode messages (<see cref="QueueMessageEncoding.None"/>, the default).
    /// </summary>
    public static FoundatioMessagingBuilder UseAzureStorageQueues(this FoundatioMessagingBuilder builder, QueueServiceClient service, AzureStorageQueueMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(service);
        return builder.UseTransport(sp => new AzureStorageQueueMessageTransport(service, WithServices(sp, options ?? new AzureStorageQueueMessageTransportOptions())));
    }

    private static AzureStorageQueueMessageTransport Create(IServiceProvider services, AzureStorageQueueMessageTransportOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseAzureStorageQueues returned null.");

        options = WithServices(services, options);
        if (!String.IsNullOrEmpty(options.ConnectionString) || options.ServiceUri is not null)
            return new AzureStorageQueueMessageTransport(options);

        var service = services.GetService<QueueServiceClient>()
            ?? throw new InvalidOperationException("UseAzureStorageQueues() inside AddFoundatioMessaging needs a connection: set ConnectionString, or ServiceUri with a Credential, " +
                "in its options, pass one to UseAzureStorageQueues(connectionString), or register a QueueServiceClient in the service collection.");

        return new AzureStorageQueueMessageTransport(service, options);
    }

    private static AzureStorageQueueMessageTransportOptions WithServices(IServiceProvider services, AzureStorageQueueMessageTransportOptions options) => options with
    {
        LoggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>(),
        TimeProvider = options.TimeProvider ?? services.GetService<IOptions<MessagingOptions>>()?.Value.TimeProvider ?? services.GetService<TimeProvider>()
    };
}
