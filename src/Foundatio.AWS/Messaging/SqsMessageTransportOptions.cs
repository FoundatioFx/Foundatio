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
    /// Makes every queue, topic and subscription an SQS FIFO queue or SNS FIFO topic (names end in ".fifo"), so
    /// messages with the same <see cref="OutgoingMessage.GroupKey"/> are delivered in order and a repeated
    /// <see cref="OutgoingMessage.DeduplicationId"/> is dropped within SQS's 5-minute window.
    /// </summary>
    /// <remarks>
    /// A message without a group key is its own group, so ungrouped messages are not ordered or held behind each
    /// other. A message without a deduplication id is never deduplicated. FIFO queues cannot delay single messages,
    /// so a delayed message is hidden when it is first received and holds back the later messages of its group until
    /// it is due. Dead letters are not ordered: each is its own group, so all of a group's dead letters can be
    /// inspected and replayed together. FIFO queues and topics have lower throughput limits than standard ones, and
    /// an SNS FIFO topic allows fewer subscriptions, which includes instance subscriptions. A transport with this
    /// option does not use the queues and topics of one without it.
    /// </remarks>
    public bool Fifo { get; init; }

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
