using System;
using Azure.Messaging.ServiceBus;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// The Service Bus entity a receiver reads: a queue, or a subscription of a topic, or the dead-letter queue of either.
/// </summary>
/// <param name="Queue">The queue name, for a queue or its dead-letter queue.</param>
/// <param name="Topic">The topic name, for a subscription or its dead-letter queue.</param>
/// <param name="Subscription">The subscription name.</param>
/// <param name="DeadLetter">Whether the receiver reads the entity's dead-letter queue.</param>
/// <param name="Instance">Whether the subscription is an instance subscription, deleted when its receiver is disposed.</param>
/// <param name="LockDuration">The entity's lock duration, when the transport read it.</param>
internal sealed record AzureServiceBusReceiverEntity(string? Queue, string? Topic, string? Subscription, bool DeadLetter, bool Instance, TimeSpan? LockDuration)
{
    public static AzureServiceBusReceiverEntity ForQueue(string queue, TimeSpan? lockDuration, bool deadLetter = false) =>
        new(queue, null, null, deadLetter, false, lockDuration);

    public static AzureServiceBusReceiverEntity ForSubscription(string topic, string subscription, TimeSpan? lockDuration, bool deadLetter = false, bool instance = false) =>
        new(null, topic, subscription, deadLetter, instance, lockDuration);

    /// <summary>Where a retry of one of its messages is sent: the queue itself, or the topic with this subscription as the target.</summary>
    public string SendPath => Queue ?? Topic!;

    public ServiceBusReceiver CreateReceiver(ServiceBusClient client, ServiceBusReceiveMode mode = ServiceBusReceiveMode.PeekLock)
    {
        var options = new ServiceBusReceiverOptions
        {
            ReceiveMode = mode,
            PrefetchCount = 0,
            SubQueue = DeadLetter ? SubQueue.DeadLetter : SubQueue.None
        };

        return Queue is not null ? client.CreateReceiver(Queue, options) : client.CreateReceiver(Topic!, Subscription!, options);
    }

    public override string ToString() => (Queue ?? $"{Topic}/Subscriptions/{Subscription}") + (DeadLetter ? "/$DeadLetterQueue" : String.Empty);
}
