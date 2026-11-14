using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Foundatio.Messaging.Transport;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// A session-enabled queue in memory, behind the Service Bus client calls the session receiver makes, so tests can hold
/// more sessions than the emulator allows and count what the receiver asks of the broker. Sessions are handed out in
/// the order they became available, as the emulator does.
/// </summary>
internal sealed class FakeServiceBusClient : ServiceBusClient
{
    private static readonly TimeSpan AcceptPollInterval = TimeSpan.FromMilliseconds(5);

    private readonly object _gate = new();
    private readonly List<FakeServiceBusSession> _sessions = [];
    private readonly LinkedList<FakeServiceBusSession> _available = new();
    private long _sequence;
    private int _accepts;
    private int _renewals;

    /// <summary>How long every receive takes before it returns what the session has.</summary>
    public TimeSpan ReceiveLatency { get; init; }

    /// <summary>How long a session lock lasts after an accept or a renewal.</summary>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Makes every session lock renewal wait until it is cancelled, as during a network partition.</summary>
    public bool HangRenewals { get; init; }

    public int Accepts => Volatile.Read(ref _accepts);

    public int Renewals => Volatile.Read(ref _renewals);

    public IReadOnlyList<FakeServiceBusSession> Sessions
    {
        get { lock (_gate) return [.. _sessions]; }
    }

    /// <summary>How many sessions a receiver holds locked now.</summary>
    public int LockedSessions
    {
        get { lock (_gate) return _sessions.Count(s => s.IsLocked); }
    }

    /// <summary>Adds a session holding messages with <paramref name="messageIds"/>, each due at <paramref name="deliverAt"/> if set.</summary>
    public FakeServiceBusSession AddSession(string sessionId, IEnumerable<string> messageIds, DateTimeOffset? deliverAt = null)
    {
        var session = new FakeServiceBusSession(this, sessionId);
        lock (_gate)
        {
            foreach (string messageId in messageIds)
            {
                var properties = new Dictionary<string, object>();
                if (deliverAt is { } at)
                    properties[AzureServiceBusMessageTransportCodec.DeliverAtProperty] = at.ToUnixTimeMilliseconds();

                session.Enqueue(ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString(messageId), messageId: messageId, sessionId: sessionId,
                    properties: properties, lockTokenGuid: Guid.NewGuid(), deliveryCount: 1, sequenceNumber: ++_sequence, enqueuedTime: DateTimeOffset.UtcNow));
            }

            _sessions.Add(session);
            _available.AddLast(session);
        }

        return session;
    }

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string queueName, ServiceBusSessionReceiverOptions? options = null, CancellationToken cancellationToken = default) =>
        AcceptAsync(cancellationToken);

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string topicName, string subscriptionName, ServiceBusSessionReceiverOptions? options = null, CancellationToken cancellationToken = default) =>
        AcceptAsync(cancellationToken);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Puts a session that was released and still has messages back in line for the next accept.</summary>
    internal void Release(FakeServiceBusSession session)
    {
        lock (_gate)
        {
            if (session.HasMessages && !_available.Contains(session))
                _available.AddLast(session);
        }
    }

    internal object Gate => _gate;

    internal void CountRenewal() => Interlocked.Increment(ref _renewals);

    private async Task<ServiceBusSessionReceiver> AcceptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_available.First is { } next)
                {
                    _available.RemoveFirst();
                    _accepts++;
                    return next.Value.Lock();
                }
            }

            await Task.Delay(AcceptPollInterval, cancellationToken);
        }
    }
}
