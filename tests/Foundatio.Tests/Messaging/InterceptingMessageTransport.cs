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

    public TransportCapabilities Capabilities { get; } = capabilities ?? inner.Capabilities;

    /// <summary>Runs before a receiver is opened.</summary>
    public Func<MessageSource, CancellationToken, Task>? OnCreateReceiver { get; set; }

    /// <summary>Runs before each <see cref="IMessageTransport.SendAsync"/>.</summary>
    public Action<MessageDestination, IReadOnlyList<OutgoingMessage>>? OnSend { get; set; }

    /// <summary>Runs before each receive, with the requested batch size and wait.</summary>
    public Action<MessageSource, int, TimeSpan>? OnReceive { get; set; }

    /// <summary>Runs before each renewal reaches the inner receiver.</summary>
    public Func<IncomingMessage, CancellationToken, Task>? OnRenew { get; set; }

    /// <summary>Runs before each dead-letter reaches the inner receiver.</summary>
    public Func<IncomingMessage, string, Task>? OnDeadLetter { get; set; }

    /// <summary>Runs before each complete, abandon or dead-letter, with the operation name.</summary>
    public Action<string, IncomingMessage>? OnSettle { get; set; }

    public int ReceiversCreated => Volatile.Read(ref _receiversCreated);

    public int OpenReceivers => _receivers.Count;

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

        var receiver = new Receiver(this, await inner.CreateReceiverAsync(source, options, cancellationToken));
        _receivers[receiver] = 0;
        Interlocked.Increment(ref _receiversCreated);
        return receiver;
    }

    public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default) => inner.DeleteAsync(source, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Receiver(InterceptingMessageTransport transport, IMessageReceiver inner) : IMessageReceiver
    {
        private readonly CancellationTokenSource _fault = new();

        public MessageSource Source => inner.Source;

        public void Fault() => _fault.Cancel();

        public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
        {
            transport.OnReceive?.Invoke(Source, maxMessages, maxWait);
            if (_fault.IsCancellationRequested)
                throw new MessageReceiverFaultedException("This receiver can never deliver again");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _fault.Token);
            try
            {
                return await inner.ReceiveAsync(maxMessages, maxWait, linked.Token);
            }
            catch (OperationCanceledException) when (_fault.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new MessageReceiverFaultedException("This receiver can never deliver again");
            }
        }

        public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        {
            transport.OnSettle?.Invoke("complete", message);
            return inner.CompleteAsync(message, cancellationToken);
        }

        public Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        {
            transport.OnSettle?.Invoke("abandon", message);
            return inner.AbandonAsync(message, delay, cancellationToken);
        }

        public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
        {
            transport.OnSettle?.Invoke("dead-letter", message);
            if (transport.OnDeadLetter is { } onDeadLetter)
                await onDeadLetter(message, reason);

            await inner.DeadLetterAsync(message, reason, cancellationToken);
        }

        public async Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        {
            if (transport.OnRenew is { } onRenew)
                await onRenew(message, cancellationToken);

            await inner.RenewAsync(message, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            transport._receivers.TryRemove(this, out _);
            return inner.DisposeAsync();
        }
    }
}
