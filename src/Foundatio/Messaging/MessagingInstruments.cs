using System;
using System.Diagnostics.Metrics;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>
/// Foundatio's messaging metrics, and the attribute names its metrics and spans use, after the OpenTelemetry
/// messaging semantic conventions where one applies and under <c>foundatio.</c> where none does.
/// </summary>
internal static class MessagingInstruments
{
    public const string System = "messaging.system";
    public const string OperationType = "messaging.operation.type";
    public const string OperationName = "messaging.operation.name";
    public const string DestinationName = "messaging.destination.name";
    public const string SubscriptionName = "messaging.destination.subscription.name";
    public const string ConsumerGroupName = "messaging.consumer.group.name";
    public const string MessageId = "messaging.message.id";
    public const string ConversationId = "messaging.message.conversation_id";
    public const string BatchMessageCount = "messaging.batch.message_count";
    public const string ErrorType = "error.type";
    public const string MessageType = "foundatio.message.type";
    public const string SourceKind = "foundatio.source.kind";
    public const string Outcome = "foundatio.outcome";

    public static readonly Counter<long> Sent = FoundatioDiagnostics.Meter.CreateCounter<long>("messaging.client.sent.messages", "{message}", "Messages a producer sent or published, or tried to: failed sends carry error.type");
    public static readonly Counter<long> Handled = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.handled", "{message}", "Messages handled, by outcome");
    public static readonly Histogram<double> Duration = FoundatioDiagnostics.Meter.CreateHistogram<double>("messaging.process.duration", "s", "Time spent handling a message");
    public static readonly Counter<long> DeadLettered = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.dead_lettered", "{message}", "Messages moved to a dead-letter queue");
    public static readonly Counter<long> Unmatched = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.unmatched", "{message}", "Messages received without a matching handler");
    public static readonly Counter<long> Discarded = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.discarded", "{message}", "Messages discarded because their time to live passed");

    /// <summary>
    /// The <c>messaging.system</c> of a transport: the OpenTelemetry name of its broker (<c>kafka</c>, <c>rabbitmq</c>,
    /// <c>aws_sqs</c>, <c>servicebus</c>), or for others the lowercase name of its class without <c>MessageTransport</c>
    /// (<c>redis</c>, <c>inmemory</c>).
    /// </summary>
    public static string GetSystemName(IMessageTransport transport)
    {
        string name = transport.GetType().Name;
        if (name.EndsWith("MessageTransport", StringComparison.Ordinal) && name.Length > "MessageTransport".Length)
            name = name[..^"MessageTransport".Length];

        return name.ToLowerInvariant() switch
        {
            "sqs" => "aws_sqs",
            "azureservicebus" => "servicebus",
            var other => other
        };
    }

    /// <summary>The <c>foundatio.source.kind</c> of a source: <c>queue</c>, <c>subscription</c>, <c>instance</c> or <c>deadletter</c>.</summary>
    public static string GetSourceKind(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => "queue",
        MessageSourceKind.Subscription => "subscription",
        MessageSourceKind.InstanceSubscription => "instance",
        _ => "deadletter"
    };
}
