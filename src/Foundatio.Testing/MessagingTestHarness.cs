using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;

namespace Foundatio.Messaging.Testing;

/// <summary>
/// One recorded message movement: a send/publish accepted by the transport, or a settlement (handled, abandoned for
/// retry, dead-lettered) of a delivered message.
/// </summary>
public sealed record RecordedMessage
{
    public required string Destination { get; init; }

    /// <summary>
    /// The role the message was sent to (Queue for sends, Topic for publishes). Settlement recordings always report
    /// Queue: every delivery settles on a queue-shaped channel, including topic deliveries via their subscriptions.
    /// </summary>
    public required DestinationRole Role { get; init; }

    public string? MessageType { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public MessageHeaders Headers { get; init; } = MessageHeaders.Empty;

    /// <summary>The dead-letter reason, for dead-lettered recordings.</summary>
    public string? Reason { get; init; }

    /// <summary>The delivery count at settlement, for settlement recordings.</summary>
    public int Attempts { get; init; }
}

/// <summary>
/// Deterministic messaging tests without sleeps: the harness runs the real bus over a recording in-memory transport,
/// so tests act (send/publish), <see cref="WaitForIdleAsync"/> until every queue and in-flight handler drains, then
/// assert on what actually happened — including the core retry/dead-letter path (a message redelivered N times and
/// then dead-lettered is directly assertable).
/// <code>
/// var services = new ServiceCollection();
/// services.AddFoundatio()
///     .Messaging.UseTestHarness()
///     .Messaging.AddHandler&lt;OrderPlaced, SendConfirmationHandler&gt;();
/// // start hosted services, then:
/// await bus.PublishAsync(new OrderPlaced(42));
/// await harness.WaitForIdleAsync();
/// Assert.Single(harness.Published&lt;OrderPlaced&gt;());
/// Assert.Empty(harness.DeadLetteredMessages);
/// </code>
/// </summary>
public sealed class MessagingTestHarness : IAsyncDisposable
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly RecordingMessageTransport _transport;
    private readonly ISerializer _serializer;
    private readonly IMessageTypeRegistry _typeRegistry;

    public MessagingTestHarness(ISerializer? serializer = null, IMessageTypeRegistry? typeRegistry = null, TimeProvider? timeProvider = null)
    {
        _serializer = serializer ?? DefaultSerializer.Instance;
        _typeRegistry = typeRegistry ?? new MessageTypeRegistry();
        _transport = new RecordingMessageTransport(timeProvider);
    }

    /// <summary>The transport to run the bus over (an in-memory transport that records every movement).</summary>
    public IMessageTransport Transport => _transport;

    /// <summary>Every message accepted by a queue-role send (a command on its way to one handler).</summary>
    public IReadOnlyList<RecordedMessage> SentMessages => _transport.Sent;

    /// <summary>Every message accepted by a topic-role send (an event on its way to each subscriber).</summary>
    public IReadOnlyList<RecordedMessage> PublishedMessages => _transport.Published;

    /// <summary>Every delivered message that settled as completed (handled successfully or auto-acked).</summary>
    public IReadOnlyList<RecordedMessage> HandledMessages => _transport.Handled;

    /// <summary>Every delivered message returned for redelivery (a retry).</summary>
    public IReadOnlyList<RecordedMessage> AbandonedMessages => _transport.Abandoned;

    /// <summary>Every delivered message that settled terminally into the dead-letter sink.</summary>
    public IReadOnlyList<RecordedMessage> DeadLetteredMessages => _transport.DeadLettered;

    /// <summary>The sent (queue-role) messages of type <typeparamref name="T"/>, deserialized.</summary>
    public IReadOnlyList<T> Sent<T>() where T : class => Deserialize<T>(_transport.Sent);

    /// <summary>The published (topic-role) messages of type <typeparamref name="T"/>, deserialized.</summary>
    public IReadOnlyList<T> Published<T>() where T : class => Deserialize<T>(_transport.Published);

    /// <summary>The successfully handled messages of type <typeparamref name="T"/>, deserialized.</summary>
    public IReadOnlyList<T> Handled<T>() where T : class => Deserialize<T>(_transport.Handled);

    /// <summary>The dead-lettered messages of type <typeparamref name="T"/>, deserialized.</summary>
    public IReadOnlyList<T> DeadLettered<T>() where T : class => Deserialize<T>(_transport.DeadLettered);

    /// <summary>
    /// Waits until the transport is quiescent — every known destination has nothing queued and nothing in flight —
    /// so assertions observe the final state. Returns quickly when already idle (fast negative assertions). Throws
    /// <see cref="TimeoutException"/> naming the still-busy destinations when the timeout (default 30s) lapses.
    /// Store-parked work (delayed sends / delayed retries through a runtime store) is not transport activity; drain
    /// it explicitly via the job schedule processor before waiting.
    /// </summary>
    public async Task WaitForIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        long deadline = Environment.TickCount64 + (long)(timeout ?? DefaultIdleTimeout).TotalMilliseconds;
        int stableChecks = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = await _transport.GetPendingAsync(cancellationToken).ConfigureAwait(false);

            if (pending.Count == 0)
            {
                // Require two consecutive idle observations: a settling message can synchronously cascade into a new
                // send, which a single snapshot could miss.
                if (++stableChecks >= 2)
                    return;
            }
            else
            {
                stableChecks = 0;

                if (Environment.TickCount64 >= deadline)
                {
                    var detail = new StringBuilder("The message bus did not become idle in time. Still busy: ");
                    detail.AppendJoin(", ", pending.Select(p => $"{p.Name} (queued={p.Queued}, working={p.Working})"));
                    throw new TimeoutException(detail.ToString());
                }
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private IReadOnlyList<T> Deserialize<T>(IReadOnlyList<RecordedMessage> recordings) where T : class
    {
        string typeName = _typeRegistry.GetName(typeof(T));
        return recordings
            .Where(r => String.Equals(r.MessageType, typeName, StringComparison.Ordinal))
            .Select(r => _serializer.Deserialize(r.Body, typeof(T)) as T)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();
    }
}
