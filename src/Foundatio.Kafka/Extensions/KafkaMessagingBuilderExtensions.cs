using System;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="KafkaMessageTransport"/> as the transport for Foundatio messaging.</summary>
public static class KafkaMessagingBuilderExtensions
{
    /// <summary>
    /// Uses Kafka as the message transport, with the default options. The service provider creates and disposes the
    /// transport.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="bootstrapServers">The brokers to connect to, for example "localhost:9092".</param>
    public static FoundatioMessagingBuilder UseKafka(this FoundatioMessagingBuilder builder, string bootstrapServers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);
        return builder.UseKafka(new KafkaMessageTransportOptions { BootstrapServers = bootstrapServers });
    }

    /// <summary>Uses Kafka as the message transport. The service provider creates and disposes the transport.</summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// The transport's options. A <see cref="KafkaMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="KafkaMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider.
    /// </param>
    public static FoundatioMessagingBuilder UseKafka(this FoundatioMessagingBuilder builder, KafkaMessageTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, options));
    }

    /// <summary>
    /// Uses Kafka as the message transport, with options built from the service provider, for example from
    /// configuration. The service provider creates and disposes the transport.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// Builds the transport's options. A <see cref="KafkaMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="KafkaMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider.
    /// </param>
    public static FoundatioMessagingBuilder UseKafka(this FoundatioMessagingBuilder builder, Func<IServiceProvider, KafkaMessageTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, options(sp)));
    }

    private static KafkaMessageTransport Create(IServiceProvider services, KafkaMessageTransportOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseKafka returned null.");

        return new KafkaMessageTransport(options with
        {
            LoggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>(),
            TimeProvider = options.TimeProvider ?? services.GetService<TimeProvider>()
        });
    }
}
