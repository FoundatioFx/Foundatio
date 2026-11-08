using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

public sealed record SqsMessageTransportOptions
{
    /// <summary>
    /// Used by <see cref="SqsMessageTransport(SqsMessageTransportOptions)"/> to create the SQS and SNS clients, for example
    /// "AccessKey=xxx;SecretKey=xxx;Region=us-east-1" or "ServiceUrl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx".
    /// When empty, the default AWS credential and region resolution is used.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Prepended to every queue and topic name, so several environments can share an AWS account. Letters, digits,
    /// '_' and '-' only, at most 32 characters.
    /// </summary>
    public string NamePrefix { get; init; } = String.Empty;

    /// <summary>
    /// How long an instance subscription queue survives without a heartbeat. Its receiver tags the queue every third
    /// of this time; once the heartbeat is older than this (the process stopped), the next instance receiver for the
    /// topic unsubscribes and deletes the queue.
    /// </summary>
    public TimeSpan InstanceSubscriptionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Used to turn <see cref="OutgoingMessage.DeliverAt"/> into a delay and to write instance heartbeats.</summary>
    public TimeProvider? TimeProvider { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }
}
