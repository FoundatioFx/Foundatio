using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;

namespace Foundatio.Tests.Hosting;

/// <summary>An in-memory transport whose receivers can be made to fail, to drive processor health through its states.</summary>
public sealed class FlakyMessageTransport(TimeProvider timeProvider) : IMessageTransport
{
    private readonly InMemoryMessageTransport _inner = new(timeProvider);
    private int _receiversCreated;

    /// <summary>Opening a receiver throws.</summary>
    public bool FailConnect { get; set; }

    /// <summary>Receiving throws.</summary>
    public bool FailReceive { get; set; }

    /// <summary>Receiving throws <see cref="MessageReceiverFaultedException"/>, as a receiver whose client died does.</summary>
    public bool FaultReceive { get; set; }

    /// <summary>Receiving returns null, which a correct transport never does, so the receive loop crashes.</summary>
    public bool ReturnNull { get; set; }

    /// <summary>How many receivers have been opened.</summary>
    public int ReceiversCreated => Volatile.Read(ref _receiversCreated);

    public TransportCapabilities Capabilities => _inner.Capabilities;

    public Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
        => _inner.SendAsync(destination, messages, cancellationToken);

    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        if (FailConnect)
            throw new InvalidOperationException("The broker is unreachable");

        var receiver = new Receiver(this, await _inner.CreateReceiverAsync(source, options, cancellationToken));
        Interlocked.Increment(ref _receiversCreated);
        return receiver;
    }

    public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default) => _inner.DeleteAsync(source, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class Receiver(FlakyMessageTransport transport, IMessageReceiver inner) : IMessageReceiver
    {
        public MessageSource Source => inner.Source;

        public Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
        {
            if (transport.ReturnNull)
                return Task.FromResult<IReadOnlyList<IncomingMessage>>(null!);
            if (transport.FailReceive)
                throw new InvalidOperationException("The connection was reset");
            if (transport.FaultReceive)
                throw new MessageReceiverFaultedException("The consumer hit a fatal error");

            return inner.ReceiveAsync(maxMessages, maxWait, cancellationToken);
        }

        public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default) => inner.CompleteAsync(message, cancellationToken);
        public Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default) => inner.AbandonAsync(message, delay, cancellationToken);
        public Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default) => inner.DeadLetterAsync(message, reason, cancellationToken);
        public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default) => inner.RenewAsync(message, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
