using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Azure.Messaging.ServiceBus;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// One session of a <see cref="FakeServiceBusClient"/>: its messages in sequence order, which of them a receiver holds
/// locked, and the receiver that holds the session, if any.
/// </summary>
internal sealed class FakeServiceBusSession(FakeServiceBusClient client, string sessionId)
{
    private readonly List<ServiceBusReceivedMessage> _available = [];
    private readonly List<ServiceBusReceivedMessage> _locked = [];
    private FakeServiceBusSessionReceiver? _holder;
    private int _receives;

    public string SessionId { get; } = sessionId;

    /// <summary>How many receives were made from this session, by every receiver that held it.</summary>
    public int Receives => Volatile.Read(ref _receives);

    public bool IsLocked
    {
        get { lock (client.Gate) return _holder is not null; }
    }

    public bool HasMessages
    {
        get { lock (client.Gate) return _available.Count > 0 || _locked.Count > 0; }
    }

    internal void Enqueue(ServiceBusReceivedMessage message) => _available.Add(message);

    internal FakeServiceBusSessionReceiver Lock()
    {
        _holder = new FakeServiceBusSessionReceiver(client, this, DateTimeOffset.UtcNow + client.LockDuration);
        return _holder;
    }

    internal IReadOnlyList<ServiceBusReceivedMessage> Take(FakeServiceBusSessionReceiver receiver, int maxMessages)
    {
        Interlocked.Increment(ref _receives);
        lock (client.Gate)
        {
            ThrowIfNotHeldBy(receiver);
            var taken = _available.Take(maxMessages).ToList();
            _available.RemoveRange(0, taken.Count);
            _locked.AddRange(taken);
            return taken;
        }
    }

    internal void Complete(FakeServiceBusSessionReceiver receiver, ServiceBusReceivedMessage message)
    {
        lock (client.Gate)
        {
            ThrowIfNotHeldBy(receiver);
            if (!_locked.Remove(message))
                throw new ServiceBusException("The message is not locked.", ServiceBusFailureReason.MessageLockLost);
        }
    }

    /// <summary>Makes a locked message available again, first in line, with its delivery counted and <paramref name="properties"/> applied.</summary>
    internal void Abandon(FakeServiceBusSessionReceiver receiver, ServiceBusReceivedMessage message, IDictionary<string, object>? properties)
    {
        lock (client.Gate)
        {
            ThrowIfNotHeldBy(receiver);
            if (!_locked.Remove(message))
                throw new ServiceBusException("The message is not locked.", ServiceBusFailureReason.MessageLockLost);

            var merged = message.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value);
            foreach (var property in properties ?? new Dictionary<string, object>())
                merged[property.Key] = property.Value;

            Return(ServiceBusModelFactory.ServiceBusReceivedMessage(message.Body, messageId: message.MessageId, sessionId: message.SessionId, properties: merged,
                lockTokenGuid: Guid.NewGuid(), deliveryCount: message.DeliveryCount + 1, sequenceNumber: message.SequenceNumber, enqueuedTime: message.EnqueuedTime));
        }
    }

    internal DateTimeOffset Renew(FakeServiceBusSessionReceiver receiver)
    {
        lock (client.Gate)
        {
            ThrowIfNotHeldBy(receiver);
            return DateTimeOffset.UtcNow + client.LockDuration;
        }
    }

    /// <summary>Releases the session and the messages locked in it without counting a delivery, as closing a session receiver does.</summary>
    internal void Close(FakeServiceBusSessionReceiver receiver)
    {
        lock (client.Gate)
        {
            if (_holder != receiver)
                return;

            _holder = null;
            foreach (var message in _locked)
                Return(message);

            _locked.Clear();
        }

        client.Release(this);
    }

    private void Return(ServiceBusReceivedMessage message)
    {
        int index = _available.FindIndex(m => m.SequenceNumber > message.SequenceNumber);
        _available.Insert(index < 0 ? _available.Count : index, message);
    }

    private void ThrowIfNotHeldBy(FakeServiceBusSessionReceiver receiver)
    {
        if (_holder != receiver)
            throw new ServiceBusException($"The lock of session {SessionId} was lost.", ServiceBusFailureReason.SessionLockLost);
    }
}
