using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class InMemoryMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    protected override IMessageTransport CreateTransport() => new InMemoryMessageTransport();

    /// <summary>Separate in-memory transports share nothing, so another process shares this test's instance instead.</summary>
    protected override IMessageTransport CreatePeerTransport() => new ProcessView(Transport);

    /// <summary>One process's view of a shared transport: disposing it closes only the receivers it opened.</summary>
    private sealed class ProcessView(IMessageTransport broker) : IMessageTransport
    {
        private readonly ConcurrentDictionary<IMessageReceiver, byte> _receivers = new();
        private int _disposed;

        public TransportCapabilities Capabilities => broker.Capabilities;

        public Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return broker.SendAsync(destination, messages, cancellationToken);
        }

        public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var receiver = await broker.CreateReceiverAsync(source, options, cancellationToken);
            _receivers.TryAdd(receiver, 0);
            return receiver;
        }

        public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return broker.DeleteAsync(source, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            foreach (var receiver in _receivers.Keys)
                await receiver.DisposeAsync();
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    }
}
