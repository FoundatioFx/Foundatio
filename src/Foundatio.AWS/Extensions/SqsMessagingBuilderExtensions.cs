using System;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="SqsMessageTransport"/> as the transport for Foundatio messaging.</summary>
public static class SqsMessagingBuilderExtensions
{
    /// <summary>
    /// Uses SQS and SNS as the message transport, connecting with <paramref name="connectionString"/> and the default
    /// options. The service provider creates and disposes the transport and its clients.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="connectionString">
    /// For example "AccessKey=xxx;SecretKey=xxx;Region=us-east-1" or "ServiceUrl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx".
    /// </param>
    public static FoundatioMessagingBuilder UseSqs(this FoundatioMessagingBuilder builder, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseSqs(new SqsMessageTransportOptions { ConnectionString = connectionString });
    }

    /// <summary>
    /// Uses SQS and SNS as the message transport. With a connection string in <paramref name="options"/>, the service
    /// provider creates and disposes the transport and its clients. Without one, the transport uses the
    /// <see cref="IAmazonSQS"/> and <see cref="IAmazonSimpleNotificationService"/> registered in the service collection
    /// (for example with <c>AddAWSService</c>), which stay yours, or, when neither is registered, clients it creates
    /// with the default AWS credential and region resolution.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// The transport's options. A <see cref="SqsMessageTransportOptions.LoggerFactory"/> or <see cref="SqsMessageTransportOptions.TimeProvider"/>
    /// left unset is taken from the service provider: the clock is <see cref="MessagingOptions.TimeProvider"/> when that is set.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The options have no connection string and only one of <see cref="IAmazonSQS"/> and
    /// <see cref="IAmazonSimpleNotificationService"/> is registered when the transport is created.
    /// </exception>
    public static FoundatioMessagingBuilder UseSqs(this FoundatioMessagingBuilder builder, SqsMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseTransport(sp => Create(sp, options ?? new SqsMessageTransportOptions()));
    }

    /// <summary>
    /// Uses SQS and SNS as the message transport, with options built from the service provider, for example from
    /// configuration. Clients are chosen as <see cref="UseSqs(FoundatioMessagingBuilder, SqsMessageTransportOptions?)"/> chooses them.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="options">
    /// Builds the transport's options. A <see cref="SqsMessageTransportOptions.LoggerFactory"/> or <see cref="SqsMessageTransportOptions.TimeProvider"/>
    /// left unset is taken from the service provider: the clock is <see cref="MessagingOptions.TimeProvider"/> when that is set.
    /// </param>
    public static FoundatioMessagingBuilder UseSqs(this FoundatioMessagingBuilder builder, Func<IServiceProvider, SqsMessageTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseTransport(sp => Create(sp, options(sp)));
    }

    /// <summary>
    /// Uses SQS and SNS as the message transport, on <paramref name="sqs"/> and <paramref name="sns"/>. The service
    /// provider creates and disposes the transport; the clients stay yours.
    /// </summary>
    /// <param name="builder">The messaging builder.</param>
    /// <param name="sqs">The SQS client.</param>
    /// <param name="sns">The SNS client, for topics and subscriptions.</param>
    /// <param name="options">
    /// The transport's options; its connection string is not used. A <see cref="SqsMessageTransportOptions.LoggerFactory"/> or
    /// <see cref="SqsMessageTransportOptions.TimeProvider"/> left unset is taken from the service provider: the clock is
    /// <see cref="MessagingOptions.TimeProvider"/> when that is set.
    /// </param>
    public static FoundatioMessagingBuilder UseSqs(this FoundatioMessagingBuilder builder, IAmazonSQS sqs, IAmazonSimpleNotificationService sns, SqsMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(sns);
        return builder.UseTransport(sp => new SqsMessageTransport(sqs, sns, WithServices(sp, options ?? new SqsMessageTransportOptions())));
    }

    private static SqsMessageTransport Create(IServiceProvider services, SqsMessageTransportOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseSqs returned null.");

        options = WithServices(services, options);
        if (!String.IsNullOrEmpty(options.ConnectionString))
            return new SqsMessageTransport(options);

        var sqs = services.GetService<IAmazonSQS>();
        var sns = services.GetService<IAmazonSimpleNotificationService>();
        if (sqs is null && sns is null)
            return new SqsMessageTransport(options);

        if (sqs is null || sns is null)
        {
            throw new InvalidOperationException($"UseSqs() found an {(sqs is null ? nameof(IAmazonSimpleNotificationService) : nameof(IAmazonSQS))} but no " +
                $"{(sqs is null ? nameof(IAmazonSQS) : nameof(IAmazonSimpleNotificationService))} in the service collection. Register both " +
                "(for example AddAWSService<IAmazonSQS>() and AddAWSService<IAmazonSimpleNotificationService>()), pass both to UseSqs(sqs, sns), " +
                "or set ConnectionString in its options.");
        }

        return new SqsMessageTransport(sqs, sns, options);
    }

    private static SqsMessageTransportOptions WithServices(IServiceProvider services, SqsMessageTransportOptions options) => options with
    {
        LoggerFactory = options.LoggerFactory ?? services.GetService<ILoggerFactory>(),
        TimeProvider = options.TimeProvider ?? services.GetService<IOptions<MessagingOptions>>()?.Value.TimeProvider ?? services.GetService<TimeProvider>()
    };
}
