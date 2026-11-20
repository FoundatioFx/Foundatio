using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;

namespace Foundatio.Tests.Messaging;

/// <summary>Wraps a transport so a test can delay, fail or observe each call, and fault every open receiver.</summary>
public sealed class InterceptingMessageTransport(IMessageTransport inner, TransportCapabilities? capabilities = null) : IMessageTransport
{
    private readonly ConcurrentDictionary<Receiver, byte> _receivers = new();
    private int _receiversCreated;
    private int _receivesInFlight;
    private int _maxReceivesInFlight;
    private int _receiveCalls;
    private int _unsettled;
    private int _maxUnsettled;

    public TransportCapabilities Capabilities { get; } = capabilities ?? inner.Capabilities;

    /// <summary>
    /// When set, the broker decides the lease, as a Service Bus lock does: receivers lease messages for this long whatever
    /// <see cref="ReceiverOptions.LeaseDuration"/> asks, and report when it ends in <see cref="IncomingMessage.LeaseExpiresAt"/>.
    /// </summary>
    public TimeSpan? BrokerLeaseDuration { get; init; }

    /// <summary>
    /// When true, <see cref="BrokerLeaseDuration"/> counts from when the receive or renewal was requested, as Service Bus
    /// counts a lock whose duration it knows, so a message that a long poll waited for reports less than the whole lease.
    /// </summary>
    public bool BrokerLeaseFromRequest { get; init; }

    /// <summary>The clock the reported leases are on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How long each receive takes before it reaches the inner receiver, like a broker round trip.</summary>
    public TimeSpan ReceiveDelay { get; init; }

    /// <summary>When set, the longest a receive waits for messages, whatever it asks for, like a broker with short polls.</summary>
    public TimeSpan? MaxReceiveWait { get; init; }

    /// <summary>Runs before a receiver is opened.</summary>
    public Func<MessageSource, CancellationToken, Task>? OnCreateReceiver { get; set; }

    /// <summary>Runs before each <see cref="IMessageTransport.SendAsync"/>.</summary>
    public Action<MessageDestination, IReadOnlyList<OutgoingMessage>>? OnSend { get; set; }

    /// <summary>Runs before each receive, after <see cref="ReceiveDelay"/>, with the requested batch size and wait.</summary>
    public Action<MessageSource, int, TimeSpan>? OnReceive { get; set; }

    /// <summary>Runs before each renewal reaches the inner receiver.</summary>
    public Func<IncomingMessage, CancellationToken, Task>? OnRenew { get; set; }

    /// <summary>Runs before each dead-letter reaches the inner receiver.</summary>
    public Func<IncomingMessage, string, Task>? OnDeadLetter { get; set; }

    /// <summary>Runs before each complete, abandon or dead-letter, with the operation name.</summary>
    public Action<string, IncomingMessage>? OnSettle { get; set; }

    /// <summary>Runs after <see cref="OnSettle"/>, before the settlement reaches the inner receiver, like a broker round trip.</summary>
    public Func<string, IncomingMessage, Task>? OnSettleAsync { get; set; }

    public int ReceiversCreated => Volatile.Read(ref _receiversCreated);

    public int OpenReceivers => _receivers.Count;

    /// <summary>The most <see cref="IMessageReceiver.ReceiveAsync"/> calls that were in flight at once, across every receiver.</summary>
    public int MaxReceivesInFlight => Volatile.Read(ref _maxReceivesInFlight);

    /// <summary>The <see cref="IMessageReceiver.ReceiveAsync"/> calls in flight now, across every receiver.</summary>
    public int ReceivesInFlight => Volatile.Read(ref _receivesInFlight);

    /// <summary>How many <see cref="IMessageReceiver.ReceiveAsync"/> calls have started, across every receiver.</summary>
    public int ReceiveCalls => Volatile.Read(ref _receiveCalls);

    /// <summary>
    /// The most messages that were received and not yet completed, abandoned or dead-lettered at once, across every
    /// receiver. Messages a disposed receiver released still count.
    /// </summary>
    public int MaxUnsettled => Volatile.Read(ref _maxUnsettled);

    /// <summary>Starts <see cref="MaxReceivesInFlight"/> again from the receives in flight now.</summary>
    public void ResetMaxReceivesInFlight() => Volatile.Write(ref _maxReceivesInFlight, ReceivesInFlight);

    /// <summary>Makes every open receiver throw <see cref="MessageReceiverFaultedException"/> from now on.</summary>
    public void FaultReceivers()
    {
        foreach (var receiver in _receivers.Keys)
            receiver.Fault();
    }

    public Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        OnSend?.Invoke(destination, messages);
        return inner.SendAsync(destination, messages, cancellationToken);
    }

    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        if (OnCreateReceiver is { } onCreate)
            await onCreate(source, cancellationToken);

        // Like a broker with a lease limit, honour at most the advertised limit.
        if (Capabilities.MaxLeaseDuration is { } maxLease && options.LeaseDuration > maxLease)
            options = options with { LeaseDuration = maxLease };
        if (BrokerLeaseDuration is { } brokerLease)
            options = options with { LeaseDuration = brokerLease };

        var receiver = new Receiver(this, await inner.CreateReceiverAsync(source, options, cancellationToken));
        _receivers[receiver] = 0;
        Interlocked.Increment(ref _receiversCreated);
        return receiver;
    }

    public Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default) => inner.GetStatsAsync(source, cancellationToken);

    public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default) => inner.DeleteAsync(source, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Receiver(InterceptingMessageTransport transport, IMessageReceiver inner) : IMessageReceiver
    {
        private readonly CancellationTokenSource _fault = new();

        public MessageSource Source => inner.Source;

        public void Fault() => _fault.Cancel();

        public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
        {
            var requestedAt = transport.TimeProvider.GetUtcNow();
            Interlocked.Increment(ref transport._receiveCalls);
            RaiseMax(ref transport._maxReceivesInFlight, Interlocked.Increment(ref transport._receivesInFlight));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _fault.Token);
            try
            {
                if (transport.ReceiveDelay > TimeSpan.Zero)
                    await Task.Delay(transport.ReceiveDelay, linked.Token);

                transport.OnReceive?.Invoke(Source, maxMessages, maxWait);
                if (_fault.IsCancellationRequested)
                    throw new MessageReceiverFaultedException("This receiver can never deliver again");

                var wait = transport.MaxReceiveWait is { } maxReceiveWait && maxReceiveWait < maxWait ? maxReceiveWait : maxWait;
                var messages = await inner.ReceiveAsync(maxMessages, wait, linked.Token);
                foreach (var message in messages)
                    ReportBrokerLease(message, requestedAt);

                RaiseMax(ref transport._maxUnsettled, Interlocked.Add(ref transport._unsettled, messages.Count));
                return messages;
            }
            catch (OperationCanceledException) when (_fault.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new MessageReceiverFaultedException("This receiver can never deliver again");
            }
            finally
            {
                Interlocked.Decrement(ref transport._receivesInFlight);
            }
        }

        public async Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        {
            await SettlingAsync("complete", message);
            await inner.CompleteAsync(message, cancellationToken);
            Interlocked.Decrement(ref transport._unsettled);
        }

        public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        {
            await SettlingAsync("abandon", message);
            await inner.AbandonAsync(message, delay, cancellationToken);
            Interlocked.Decrement(ref transport._unsettled);
        }

        public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
        {
            await SettlingAsync("dead-letter", message);
            if (transport.OnDeadLetter is { } onDeadLetter)
                await onDeadLetter(message, reason);

            await inner.DeadLetterAsync(message, reason, cancellationToken);
            Interlocked.Decrement(ref transport._unsettled);
        }

        public async Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        {
            if (transport.OnRenew is { } onRenew)
                await onRenew(message, cancellationToken);

            var requestedAt = transport.TimeProvider.GetUtcNow();
            await inner.RenewAsync(message, cancellationToken);
            ReportBrokerLease(message, requestedAt);
        }

        private async Task SettlingAsync(string operation, IncomingMessage message)
        {
            transport.OnSettle?.Invoke(operation, message);
            if (transport.OnSettleAsync is { } onSettle)
                await onSettle(operation, message);
        }

        private static void RaiseMax(ref int max, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, value, current) != current)
            {
            }
        }

        private void ReportBrokerLease(IncomingMessage message, DateTimeOffset requestedAt)
        {
            if (transport.BrokerLeaseDuration is { } brokerLease)
                message.LeaseExpiresAt = (transport.BrokerLeaseFromRequest ? requestedAt : transport.TimeProvider.GetUtcNow()).Add(brokerLease);
        }

        public ValueTask DisposeAsync()
        {
            transport._receivers.TryRemove(this, out _);
            return inner.DisposeAsync();
        }
    }
}
