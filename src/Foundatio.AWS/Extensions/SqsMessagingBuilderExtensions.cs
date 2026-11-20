using System;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="SqsMessageTransport"/> as the transport for Foundatio messaging.</summary>
public static class SqsMessagingBuilderExtensions
{
    /// <summary>
    /// Uses SQS and SNS as the message transport. The service provider creates and disposes the transport and the
    /// clients it creates from <see cref="SqsMessageTransportOptions.ConnectionString"/>.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// The transport's options; without a connection string, the default AWS credential and region resolution is
    /// used. A <see cref="SqsMessageTransportOptions.LoggerFactory"/> or <see cref="SqsMessageTransportOptions.TimeProvider"/>
    /// left unset is taken from the service provider.
    /// </param>
    public static FoundatioMessagingBuilder UseSqs(this FoundatioMessagingBuilder builder, SqsMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseTransport(sp => Create(sp, options ?? new SqsMessageTransportOptions()));
    }

    /// <summary>
    /// Uses SQS and SNS as the message transport, with options built from the service provider, for example from
    /// configuration. The service provider creates and disposes the transport and the clients it creates from
    /// <see cref="SqsMessageTransportOptions.ConnectionString"/>.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// Builds the transport's options; without a connection string, the default AWS credential and region resolution
    /// is used. A <see cref="SqsMessageTransportOptions.LoggerFactory"/> or <see cref="SqsMessageTransportOptions.TimeProvider"/>
    /// left unset is taken from the service provider.
    /// </param>
    public static FoundatioMessagingBuilder UseSqs(this FoundatioMessagingBuilder builder, Func<IServiceProvider, SqsMessageTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, options(sp)));
    }

    private static SqsMessageTransport Create(IServiceProvider services, SqsMessageTransportOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseSqs returned null.");

        return new SqsMessageTransport(options with
        {
            LoggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>(),
            TimeProvider = options.TimeProvider ?? services.GetService<TimeProvider>()
        });
    }
}
