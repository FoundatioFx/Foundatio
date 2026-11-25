using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// A queue without sessions in memory, behind the Service Bus client calls the message receiver makes, so tests can
/// decide when a message arrives during a receive. As on the broker, a message is locked for <see cref="LockDuration"/>
/// from when a receive takes it.
/// </summary>
internal sealed class FakeServiceBusQueueClient : ServiceBusClient
{
    private readonly object _gate = new();
    private readonly Queue<string> _available = new();
    private long _sequence;
    private int _waitingReceives;
    private int _abandons;

    /// <summary>How long a message stays locked after a receive takes it.</summary>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many receives are waiting for a message to arrive.</summary>
    public int WaitingReceives => Volatile.Read(ref _waitingReceives);

    /// <summary>How many messages were abandoned.</summary>
    public int Abandons => Volatile.Read(ref _abandons);

    public void Send(string messageId)
    {
        lock (_gate)
            _available.Enqueue(messageId);
    }

    public override ServiceBusReceiver CreateReceiver(string queueName, ServiceBusReceiverOptions options) => new FakeServiceBusQueueReceiver(this);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal IReadOnlyList<ServiceBusReceivedMessage> Take(int maxMessages)
    {
        lock (_gate)
        {
            var messages = new List<ServiceBusReceivedMessage>();
            while (messages.Count < maxMessages && _available.TryDequeue(out string? messageId))
            {
                var now = DateTimeOffset.UtcNow;
                messages.Add(ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString(messageId), messageId: messageId,
                    lockTokenGuid: Guid.NewGuid(), deliveryCount: 1, sequenceNumber: ++_sequence, enqueuedTime: now, lockedUntil: now + LockDuration));
            }

            return messages;
        }
    }

    internal void Abandon(ServiceBusReceivedMessage message)
    {
        Interlocked.Increment(ref _abandons);
        Send(message.MessageId);
    }

    internal void StartWaiting() => Interlocked.Increment(ref _waitingReceives);

    internal void StopWaiting() => Interlocked.Decrement(ref _waitingReceives);
}
