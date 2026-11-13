using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Receives from one queue on a channel of its own. The broker pushes deliveries to a consumer, up to its prefetch,
/// into a buffer that <see cref="ReceiveAsync"/> hands out from.
/// </summary>
/// <remarks>
/// RabbitMQ counts every unacknowledged delivery against its consumer's prefetch, and a new prefetch only applies to
/// new consumers. So when a caller holds as many unsettled deliveries as the prefetch and asks for more, the receiver
/// starts another consumer and cancels the old one: deliveries of a cancelled consumer stay with the channel and can
/// still be settled, and the new consumer has its whole prefetch to fill the buffer.
/// </remarks>
internal sealed class RabbitMQMessageReceiver : IMessageReceiver
{
    private readonly RabbitMQMessageTransport _transport;
    private readonly string _queue;
    private readonly string? _topicExchange;
    private readonly ushort _prefetch;
    private readonly Channel<RabbitMQIncomingMessage> _buffer = Channel.CreateUnbounded<RabbitMQIncomingMessage>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly object _lock = new();
    private readonly HashSet<Consumer> _retired = [];
    private readonly SemaphoreSlim _replaceLock = new(1, 1);
    private readonly CancellationTokenSource _disposing = new();
    private IChannel? _channel;
    private Consumer? _live;
    private Exception? _fault;
    private volatile bool _channelClosed;
    private int _disposed;

    public RabbitMQMessageReceiver(RabbitMQMessageTransport transport, MessageSource source, string queue, string? topicExchange, ReceiverOptions options)
    {
        _transport = transport;
        Source = source;
        _queue = queue;
        _topicExchange = topicExchange;
        _prefetch = (ushort)Math.Clamp(options.PrefetchCount, 1, UInt16.MaxValue);
    }

    public MessageSource Source { get; }

    /// <summary>The queue this receiver consumes.</summary>
    internal string QueueName => _queue;

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>Opens the channel, declares an instance subscription's queue, and registers the first consumer.</summary>
    public async Task StartAsync(IConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();
            _channel = channel;
            channel.ChannelShutdownAsync += OnChannelShutdownAsync;

            if (_topicExchange is not null)
            {
                // Exclusive to this connection and deleted with its last consumer, so a crash leaves nothing behind.
                await channel.QueueDeclareAsync(_queue, durable: false, exclusive: true, autoDelete: true,
                    arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" }, cancellationToken: cancellationToken).AnyContext();
                await channel.QueueBindAsync(_queue, _topicExchange, String.Empty, cancellationToken: cancellationToken).AnyContext();
                if (_transport.Options.DelayedDelivery)
                    await channel.QueueBindAsync(_queue, _transport.Names.DelayDelivery, RabbitMQDelayLevels.DeliveryBindingKey(_queue), cancellationToken: cancellationToken).AnyContext();
            }

            await channel.BasicQosAsync(0, _prefetch, false, cancellationToken).AnyContext();
            _live = await StartConsumerAsync(channel, cancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException)
        {
            throw new MessageBusException($"Could not receive from {Source}: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            ThrowIfFaulted();
            var batch = TakeBuffered(maxMessages);
            if (batch.Count > 0)
                return batch;

            await ReplaceConsumerIfFullAsync().AnyContext();

            var remaining = maxWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return batch;

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
            wait.CancelAfter(remaining);
            try
            {
                if (!await _buffer.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                {
                    ThrowIfFaulted();
                    ThrowIfDisposed();
                    return batch;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ThrowIfDisposed();
                return TakeBuffered(maxMessages);
            }
        }
    }

    public async Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = Claim(message);
        try
        {
            // Never cancelled half way: a settlement whose frame went out must not be sent again.
            await _channel!.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).AnyContext();
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException)
        {
            throw LeaseLost(delivery, ex);
        }
        finally
        {
            OnSettled(delivery);
        }
    }

    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var delivery = Claim(message);
        if (Source.Kind == MessageSourceKind.DeadLetter && delay <= TimeSpan.Zero)
        {
            // A peek's release: the dead letter goes back to its place in the queue.
            await NackAsync(delivery).AnyContext();
            return;
        }

        try
        {
            await _transport.PublishCopyAsync(_queue, declareQueue: _topicExchange is null, delivery, delivery.Headers, delivery.DeliveryCount, delay, cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            ReleaseLater(delivery, ex);
            throw;
        }

        await AcknowledgeCopiedAsync(delivery).AnyContext();
    }

    public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Source.Kind == MessageSourceKind.InstanceSubscription)
            throw new InvalidOperationException("Instance subscriptions have no dead-letter queue.");

        var delivery = Claim(message);
        var headers = new Dictionary<string, string>(delivery.Headers.Count + 1, StringComparer.Ordinal);
        foreach (var (name, value) in delivery.Headers)
            headers[name] = value;
        headers[KnownHeaders.DeadLetterReason] = RabbitMQMessageTransportCodec.TruncateReason(reason);

        try
        {
            await _transport.PublishCopyAsync(_transport.Names.DeadLetter(Source), declareQueue: true, delivery, headers, 0, TimeSpan.Zero, cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            ReleaseLater(delivery, ex);
            throw;
        }

        await AcknowledgeCopiedAsync(delivery).AnyContext();
    }

    public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var delivery = AsOwnDelivery(message);
        if (_channelClosed)
            throw new MessageLeaseLostException($"The channel message {delivery.MessageId} was delivered on has closed; RabbitMQ delivers it again.");
        if (delivery.IsSettled)
            throw new MessageLeaseLostException($"Message {delivery.MessageId} was already settled.");

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _disposing.CancelAsync().AnyContext();
        _buffer.Writer.TryComplete();
        var channel = _channel;
        if (channel is not null && !_channelClosed)
        {
            try
            {
                // Deliveries buffered and never handed out go back to their place without counting as a delivery;
                // closing the channel then returns the unsettled ones, which counts.
                while (_buffer.Reader.TryRead(out var buffered))
                {
                    if (buffered.TrySettle())
                        await channel.BasicNackAsync(buffered.DeliveryTag, multiple: false, requeue: true, CancellationToken.None).AnyContext();
                }

                if (_topicExchange is not null)
                    await channel.QueueDeleteAsync(_queue, ifUnused: false, ifEmpty: false, cancellationToken: CancellationToken.None).AnyContext();

                await channel.CloseAsync().AnyContext();
            }
            catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException)
            {
                _transport.Logger.LogDebug(ex, "The channel of {Source} closed while it was being disposed", Source);
            }
        }

        if (channel is not null)
            await channel.DisposeAsync().AnyContext();

        _transport.Remove(this);
    }

    private void OnDelivered(Consumer consumer, ulong deliveryTag, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        // The client reuses the body's buffer once the handler returns.
        var decoded = RabbitMQMessageTransportCodec.Decode(properties, body.ToArray());
        var message = new RabbitMQIncomingMessage
        {
            MessageId = decoded.MessageId,
            Body = decoded.Body,
            Headers = decoded.Headers,
            GroupKey = decoded.GroupKey,
            DeliveryCount = decoded.DeliveryCount,
            EnqueuedAt = decoded.EnqueuedAt,
            Owner = this,
            Consumer = consumer,
            DeliveryTag = deliveryTag
        };

        consumer.OnDelivered();
        // A delivery that arrives once the receiver is closing is returned with its channel.
        _buffer.Writer.TryWrite(message);
    }

    private List<IncomingMessage> TakeBuffered(int maxMessages)
    {
        var batch = new List<IncomingMessage>();
        while (batch.Count < maxMessages && _buffer.Reader.TryRead(out var message))
        {
            if (!message.IsSettled)
                batch.Add(message);
        }

        return batch;
    }

    /// <summary>
    /// Starts a new consumer when the live one's prefetch is used up by deliveries already handed out, so a caller
    /// holding that many unsettled messages still receives more.
    /// </summary>
    private async Task ReplaceConsumerIfFullAsync()
    {
        var live = Volatile.Read(ref _live);
        if (live is null || live.Unsettled < _prefetch)
            return;

        lock (_lock)
        {
            if (_retired.Count >= _transport.Options.MaxRetiredConsumers)
                return;
        }

        try
        {
            await _replaceLock.WaitAsync(_disposing.Token).AnyContext();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_live != live || _fault is not null || IsDisposed || live.Unsettled < _prefetch)
                return;

            var next = await StartConsumerAsync(_channel!, _disposing.Token).AnyContext();
            lock (_lock)
            {
                live.IsRetired = true;
                if (live.Unsettled > 0)
                    _retired.Add(live);

                Volatile.Write(ref _live, next);
            }

            await _channel!.BasicCancelAsync(live.Tag!, noWait: false, _disposing.Token).AnyContext();
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or OperationCanceledException)
        {
            // The channel closed, and its shutdown faults the receiver, or the receiver is being disposed.
            _transport.Logger.LogDebug(ex, "Could not replace the consumer of {Source}", Source);
        }
        finally
        {
            _replaceLock.Release();
        }
    }

    private async Task<Consumer> StartConsumerAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var consumer = new Consumer(this, channel);
        consumer.Tag = await channel.BasicConsumeAsync(_queue, autoAck: false, consumerTag: String.Empty, noLocal: false, exclusive: false,
            arguments: null, consumer, cancellationToken).AnyContext();
        return consumer;
    }

    private RabbitMQIncomingMessage Claim(IncomingMessage message)
    {
        ThrowIfDisposed();
        var delivery = AsOwnDelivery(message);
        if (_channelClosed)
            throw new MessageLeaseLostException($"The channel message {delivery.MessageId} was delivered on has closed; RabbitMQ delivers it again.");
        if (!delivery.TrySettle())
            throw new MessageLeaseLostException($"Message {delivery.MessageId} was already settled.");

        return delivery;
    }

    private RabbitMQIncomingMessage AsOwnDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not RabbitMQIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        return delivery;
    }

    /// <summary>Acknowledges a delivery once its copy was sent. If the channel closed first, the message is delivered again too.</summary>
    private async Task AcknowledgeCopiedAsync(RabbitMQIncomingMessage delivery)
    {
        try
        {
            await _channel!.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).AnyContext();
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException)
        {
            _transport.Logger.LogDebug(ex, "The channel closed after message {MessageId} was copied; it is delivered again as well", delivery.MessageId);
        }
        finally
        {
            OnSettled(delivery);
        }
    }

    private async Task NackAsync(RabbitMQIncomingMessage delivery)
    {
        try
        {
            await _channel!.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None).AnyContext();
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException)
        {
            throw LeaseLost(delivery, ex);
        }
        finally
        {
            OnSettled(delivery);
        }
    }

    /// <summary>
    /// After a settlement failed, returns the delivery to its queue once a short delay has passed, so a broker that
    /// refuses the copy is not asked again at once. Closing the channel returns it sooner.
    /// </summary>
    private void ReleaseLater(RabbitMQIncomingMessage delivery, Exception error)
    {
        _transport.Logger.LogWarning(error, "Could not settle message {MessageId} from {Source}; it is released in {Delay}", delivery.MessageId, Source, _transport.Options.ReleaseDelay);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_transport.Options.ReleaseDelay, _transport.TimeProvider, _disposing.Token).AnyContext();
                if (!_channelClosed)
                    await _channel!.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None).AnyContext();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or AlreadyClosedException or OperationInterruptedException)
            {
                // Closing the channel returns the delivery.
            }
            finally
            {
                OnSettled(delivery);
            }
        });
    }

    private void OnSettled(RabbitMQIncomingMessage delivery)
    {
        var consumer = delivery.Consumer;
        if (consumer.OnSettled() > 0)
            return;

        lock (_lock)
        {
            if (consumer.IsRetired)
                _retired.Remove(consumer);
        }
    }

    private Task OnChannelShutdownAsync(object sender, ShutdownEventArgs args)
    {
        _channelClosed = true;
        if (!IsDisposed)
            Fault(new MessageReceiverFaultedException($"The channel of the receiver for {Source} closed: {args.ReplyCode} {args.ReplyText}"));

        return Task.CompletedTask;
    }

    private void OnCancelledByBroker()
    {
        if (!IsDisposed)
            Fault(new MessageReceiverFaultedException($"RabbitMQ cancelled the consumer of {Source}: its queue {_queue} was deleted or became unavailable."));
    }

    private void Fault(Exception error)
    {
        if (Interlocked.CompareExchange(ref _fault, error, null) is not null)
            return;

        _transport.Logger.LogWarning("{Message}; Foundatio replaces the receiver", error.Message);
        _buffer.Writer.TryComplete();
    }

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _fault) is { } fault)
            throw new MessageReceiverFaultedException(fault.Message, fault);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _transport.ThrowIfDisposed();
    }

    private static MessageLeaseLostException LeaseLost(RabbitMQIncomingMessage delivery, Exception error) =>
        new($"The channel message {delivery.MessageId} was delivered on has closed; RabbitMQ delivers it again.", error);

    /// <summary>One consumer of the receiver's channel; its unsettled deliveries count against its prefetch.</summary>
    internal sealed class Consumer : AsyncDefaultBasicConsumer
    {
        private readonly RabbitMQMessageReceiver _receiver;
        private int _unsettled;

        public Consumer(RabbitMQMessageReceiver receiver, IChannel channel) : base(channel)
        {
            _receiver = receiver;
        }

        public string? Tag { get; set; }

        /// <summary>Cancelled; kept until its deliveries are settled.</summary>
        public bool IsRetired { get; set; }

        public int Unsettled => Volatile.Read(ref _unsettled);

        public void OnDelivered() => Interlocked.Increment(ref _unsettled);

        /// <summary>Returns how many deliveries are left unsettled.</summary>
        public int OnSettled() => Interlocked.Decrement(ref _unsettled);

        public override Task HandleBasicDeliverAsync(string consumerTag, ulong deliveryTag, bool redelivered, string exchange, string routingKey,
            IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        {
            _receiver.OnDelivered(this, deliveryTag, properties, body);
            return Task.CompletedTask;
        }

        public override async Task HandleBasicCancelAsync(string consumerTag, CancellationToken cancellationToken = default)
        {
            await base.HandleBasicCancelAsync(consumerTag, cancellationToken).AnyContext();
            _receiver.OnCancelledByBroker();
        }
    }
}
