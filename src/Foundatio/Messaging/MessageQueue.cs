using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Messaging;

public enum AckMode
{
    Auto,
    Manual
}

public sealed record QueueMessageOptions
{
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public TimeSpan? Delay { get; init; }
    public DateTimeOffset? DeliverAt { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public string? CorrelationId { get; init; }
    public string? DeduplicationId { get; init; }
    public string? Destination { get; init; }
    public MessageHeaders? Headers { get; init; }
}

public sealed record QueueReceiveOptions
{
    public string? Source { get; init; }
    public TimeSpan? MaxWaitTime { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record QueueConsumerOptions
{
    public AckMode AckMode { get; init; } = AckMode.Auto;
    public string? Source { get; init; }
    public string? Key { get; init; }
    public int MaxConcurrency { get; init; } = 1;
    public int MaxAttempts { get; init; } = 5;
    public Func<int, TimeSpan>? RedeliveryBackoff { get; init; }
}

public sealed record QueueOptions
{
    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    public string ContentType { get; init; } = "application/json";
    public Func<Type, string>? DestinationResolver { get; init; }
    public Func<Type, string>? MessageTypeResolver { get; init; }
    public IJobRuntimeStore? RuntimeStore { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public interface IQueue : IAsyncDisposable
{
    Task<string> EnqueueAsync<T>(T message, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task EnqueueBatchAsync<T>(IEnumerable<T> messages, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IReceivedMessage<T>?> ReceiveAsync<T>(QueueReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IMessageConsumer> StartConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task RunConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class;
}

public interface IMessageConsumer : IAsyncDisposable
{
    string Source { get; }
    string Key { get; }
}

public interface IReceivedMessage<out T> where T : class
{
    T Message { get; }
    string Id { get; }
    MessageHeaders Headers { get; }
    string? CorrelationId { get; }
    string? MessageType { get; }
    MessagePriority Priority { get; }
    int Attempts { get; }
    bool IsHandled { get; }
    CancellationToken CancellationToken { get; }
    Task CompleteAsync(CancellationToken cancellationToken = default);
    Task AbandonAsync(CancellationToken cancellationToken = default);
    Task DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default);
    Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default);
    Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default);
}

public sealed class MessageQueue : IQueue
{
    private readonly IMessageTransport _transport;
    private readonly QueueOptions _options;
    private readonly ConcurrentDictionary<string, MessageConsumerHandle> _consumers = new(StringComparer.Ordinal);
    private int _isDisposed;

    public MessageQueue(IMessageTransport transport, QueueOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new QueueOptions();
    }

    public async Task<string> EnqueueAsync<T>(T message, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        options ??= new QueueMessageOptions();
        ValidateSendOptions(options);

        string destination = GetDestination(typeof(T), options.Destination);
        var sendOptions = CreateSendOptions(options);
        string messageId = options.DeduplicationId ?? Guid.NewGuid().ToString("N");
        var transportMessage = CreateTransportMessage(message, options, messageId);

        if (await TryScheduleDispatchAsync(ScheduledDispatchKind.QueueMessage, destination, transportMessage, sendOptions, cancellationToken).AnyContext())
            return messageId;

        var result = await _transport.SendAsync(destination, [transportMessage], sendOptions, cancellationToken).AnyContext();
        var item = result.Items.Count > 0 ? result.Items[0] : null;

        if (item is null || !item.Success)
            throw new MessageQueueException($"Unable to enqueue message to \"{destination}\": {item?.ErrorCode ?? "unknown error"}");

        return item.MessageId ?? messageId;
    }

    public async Task EnqueueBatchAsync<T>(IEnumerable<T> messages, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        options ??= new QueueMessageOptions();
        ValidateSendOptions(options);

        string destination = GetDestination(typeof(T), options.Destination);
        var sendOptions = CreateSendOptions(options);
        int index = 0;
        var transportMessages = messages.Select(message =>
        {
            ArgumentNullException.ThrowIfNull(message);
            string? messageId = options.DeduplicationId is null ? null : $"{options.DeduplicationId}:{index}";
            index++;
            return CreateTransportMessage(message, options, messageId);
        }).ToArray();

        if (transportMessages.Length == 0)
            return;

        if (await TryScheduleDispatchesAsync(ScheduledDispatchKind.QueueMessage, destination, transportMessages, sendOptions, cancellationToken).AnyContext())
            return;

        var result = await _transport.SendAsync(destination, transportMessages, sendOptions, cancellationToken).AnyContext();
        if (!result.AllSucceeded)
            throw new MessageQueueException($"Unable to enqueue {result.Items.Count(i => !i.Success)} of {result.Items.Count} messages to \"{destination}\".");
    }

    public async Task<IReceivedMessage<T>?> ReceiveAsync<T>(QueueReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ThrowIfDisposed();

        if (_transport is not ISupportsPull pull)
            throw new MessageQueueException($"Transport \"{_transport.GetType().Name}\" does not support pull receive.");

        options ??= new QueueReceiveOptions();
        string source = GetDestination(typeof(T), options.Source);
        var entries = await pull.ReceiveAsync(source, new ReceiveRequest
        {
            MaxMessages = 1,
            MaxWaitTime = options.MaxWaitTime
        }, cancellationToken).AnyContext();

        if (entries.Count == 0)
            return null;

        return await CreateReceivedMessageAsync<T>(entries[0], cancellationToken).AnyContext();
    }

    public async Task<IMessageConsumer> StartConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new QueueConsumerOptions();
        string source = GetDestination(typeof(T), options.Source);
        string key = GetConsumerKey(typeof(T), source, options.Key);

        if (_consumers.TryGetValue(key, out var existing) && !existing.IsDisposed)
            return existing;

        var handle = new MessageConsumerHandle(source, key, RemoveConsumer);
        if (!_consumers.TryAdd(key, handle))
        {
            await handle.DisposeAsync().AnyContext();
            return _consumers[key];
        }

        try
        {
            if (_transport is ISupportsPush push)
            {
                var subscription = await push.SubscribeAsync(source, async (entry, token) =>
                {
                    var received = await CreateReceivedMessageAsync<T>(entry, token).AnyContext();
                    await HandleMessageAsync(received, handler, options, token).AnyContext();
                }, new PushOptions { MaxConcurrentMessages = Math.Max(1, options.MaxConcurrency) }, cancellationToken).AnyContext();

                handle.SetPushSubscription(subscription);
                return handle;
            }

            if (_transport is not ISupportsPull pull)
                throw new MessageQueueException($"Transport \"{_transport.GetType().Name}\" does not support receiving messages.");

            handle.Start(RunPullConsumerLoopAsync(source, pull, handler, options, handle.CancellationToken));
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().AnyContext();
            throw;
        }
    }

    public async Task RunConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        await using var consumer = await StartConsumerAsync(handler, options, cancellationToken).AnyContext();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).AnyContext();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        var consumers = _consumers.Values.ToArray();
        foreach (var consumer in consumers)
            await consumer.DisposeAsync().AnyContext();

        await _transport.DisposeAsync().AnyContext();
    }

    private async Task RunPullConsumerLoopAsync<T>(string source, ISupportsPull pull, Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions options, CancellationToken cancellationToken) where T : class
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var entries = await pull.ReceiveAsync(source, new ReceiveRequest
            {
                MaxMessages = Math.Max(1, options.MaxConcurrency),
                MaxWaitTime = TimeSpan.FromSeconds(1)
            }, cancellationToken).AnyContext();

            var tasks = entries.Select(async entry =>
            {
                var received = await CreateReceivedMessageAsync<T>(entry, cancellationToken).AnyContext();
                await HandleMessageAsync(received, handler, options, cancellationToken).AnyContext();
            }).ToArray();

            await Task.WhenAll(tasks).AnyContext();
        }
    }

    private async Task<IReceivedMessage<T>> CreateReceivedMessageAsync<T>(TransportEntry entry, CancellationToken ct) where T : class
    {
        try
        {
            var message = _options.Serializer.Deserialize<T>(entry.Body);
            if (message is null)
                throw new MessageQueueException($"Message \"{entry.Id}\" deserialized to null.");

            return new ReceivedMessage<T>(_transport, entry, message, ct, _options.RuntimeStore, _options.TimeProvider);
        }
        catch (Exception ex) when (ex is not MessageQueueException)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", ct).AnyContext();
            throw new MessageQueueException($"Unable to deserialize message \"{entry.Id}\".", ex);
        }
        catch (MessageQueueException)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", ct).AnyContext();
            throw;
        }
    }

    private async Task HandleMessageAsync<T>(IReceivedMessage<T> message, Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions options, CancellationToken ct) where T : class
    {
        try
        {
            await handler(message, ct).AnyContext();

            if (options.AckMode == AckMode.Auto && !message.IsHandled)
                await message.CompleteAsync(ct).AnyContext();
        }
        catch
        {
            if (message.IsHandled)
                return;

            if (message.Attempts >= options.MaxAttempts)
            {
                await message.DeadLetterAsync("handler-error", ct).AnyContext();
                return;
            }

            TimeSpan? redeliveryDelay = options.RedeliveryBackoff?.Invoke(message.Attempts);
            if (redeliveryDelay is { } delay && delay > TimeSpan.Zero && message is ReceivedMessage<T> received)
                await received.AbandonAsync(delay, ct).AnyContext();
            else
                await message.AbandonAsync(ct).AnyContext();
        }
    }

    private TransportMessage CreateTransportMessage<T>(T message, QueueMessageOptions options, string? messageId = null) where T : class
    {
        var headers = (options.Headers ?? MessageHeaders.Empty).ToBuilder()
            .Set(KnownHeaders.MessageType, GetMessageType(typeof(T)))
            .Set(KnownHeaders.ContentType, _options.ContentType)
            .Set(KnownHeaders.Priority, options.Priority.ToString());

        if (!String.IsNullOrEmpty(options.CorrelationId))
            headers.Set(KnownHeaders.CorrelationId, options.CorrelationId);

        if (Activity.Current is { } activity)
        {
            if (!String.IsNullOrEmpty(activity.Id))
                headers.SetIfMissing(KnownHeaders.TraceParent, activity.Id);

            if (!String.IsNullOrEmpty(activity.TraceStateString))
                headers.SetIfMissing(KnownHeaders.TraceState, activity.TraceStateString);
        }

        if (options.TimeToLive is { } ttl)
            headers.Set(KnownHeaders.Expiration, _options.TimeProvider.GetUtcNow().Add(ttl).ToString("O", CultureInfo.InvariantCulture));

        return new TransportMessage
        {
            Body = _options.Serializer.SerializeToBytes(message),
            Headers = headers.Build(),
            MessageId = messageId
        };
    }

    private TransportSendOptions CreateSendOptions(QueueMessageOptions options)
    {
        return new TransportSendOptions
        {
            Priority = options.Priority,
            DeliverAt = options.DeliverAt ?? (options.Delay is { } delay ? _options.TimeProvider.GetUtcNow().Add(delay) : null),
            DeduplicationId = options.DeduplicationId
        };
    }

    private void ValidateSendOptions(QueueMessageOptions options)
    {
        if (options.Priority != MessagePriority.Normal && _transport is not ISupportsPriority)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support message priority.");

        if (options.TimeToLive is not null && _transport is not ISupportsExpiration)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support message expiration.");
    }

    private async Task<bool> TryScheduleDispatchesAsync(ScheduledDispatchKind kind, string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken cancellationToken)
    {
        if (!ShouldScheduleThroughRuntimeStore(options, out var dueUtc))
            return false;

        for (int index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            string messageId = message.MessageId ?? Guid.NewGuid().ToString("N");
            await ScheduleDispatchAsync(kind, destination, message with { MessageId = messageId }, options, dueUtc, cancellationToken).AnyContext();
        }

        return true;
    }

    private Task<bool> TryScheduleDispatchAsync(ScheduledDispatchKind kind, string destination, TransportMessage message, TransportSendOptions options, CancellationToken cancellationToken)
    {
        return TryScheduleDispatchesAsync(kind, destination, [message], options, cancellationToken);
    }

    private bool ShouldScheduleThroughRuntimeStore(TransportSendOptions options, out DateTimeOffset dueUtc)
    {
        dueUtc = options.DeliverAt.GetValueOrDefault();
        if (options.DeliverAt is null || dueUtc <= _options.TimeProvider.GetUtcNow())
            return false;

        if (_transport is ISupportsDelayedDelivery)
            return false;

        if (_options.RuntimeStore is null)
            throw new MessageQueueException($"Delayed queue delivery requires either native delayed-delivery support from transport \"{_transport.GetType().Name}\" or {nameof(QueueOptions)}.{nameof(QueueOptions.RuntimeStore)}.");

        return true;
    }

    private Task ScheduleDispatchAsync(ScheduledDispatchKind kind, string destination, TransportMessage message, TransportSendOptions options, DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        return _options.RuntimeStore!.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = message.MessageId!,
            Kind = kind,
            Destination = destination,
            Body = message.Body,
            Headers = message.Headers,
            Options = options with { DeliverAt = null },
            DueUtc = dueUtc
        }, cancellationToken);
    }

    private string GetDestination(Type messageType, string? destination)
    {
        if (!String.IsNullOrEmpty(destination))
            return destination;

        if (_options.DestinationResolver?.Invoke(messageType) is { Length: > 0 } resolved)
            return resolved;

        return messageType.GetCustomAttribute<MessageRouteAttribute>()?.Destination ?? MessageRoutingConventions.ToKebabCase(messageType.Name);
    }

    private string GetMessageType(Type messageType)
    {
        return _options.MessageTypeResolver?.Invoke(messageType) ?? messageType.FullName ?? messageType.Name;
    }

    private static string GetConsumerKey(Type messageType, string source, string? key)
    {
        return !String.IsNullOrEmpty(key)
            ? key
            : $"{source}:{messageType.FullName ?? messageType.Name}";
    }

    private async Task DeadLetterPoisonMessageAsync(TransportEntry entry, string reason, CancellationToken ct)
    {
        await ReceivedMessage<object>.DeadLetterAsync(_transport, entry, reason, ct).AnyContext();
    }

    private void RemoveConsumer(string key, MessageConsumerHandle handle)
    {
        _consumers.TryRemove(new KeyValuePair<string, MessageConsumerHandle>(key, handle));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
    }
}

internal sealed class ReceivedMessage<T> : IReceivedMessage<T> where T : class
{
    private readonly IMessageTransport _transport;
    private readonly TransportEntry _entry;
    private readonly IJobRuntimeStore? _runtimeStore;
    private readonly TimeProvider _timeProvider;
    private int _isHandled;

    public ReceivedMessage(IMessageTransport transport, TransportEntry entry, T message, CancellationToken cancellationToken, IJobRuntimeStore? runtimeStore = null, TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _entry = entry;
        _runtimeStore = runtimeStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Message = message;
        CancellationToken = cancellationToken;
    }

    public T Message { get; }
    public string Id => _entry.Id;
    public MessageHeaders Headers => _entry.Headers;
    public string? CorrelationId => Headers.GetValueOrDefault(KnownHeaders.CorrelationId);
    public string? MessageType => Headers.GetValueOrDefault(KnownHeaders.MessageType);
    public MessagePriority Priority => Enum.TryParse(Headers.GetValueOrDefault(KnownHeaders.Priority), ignoreCase: true, out MessagePriority priority) ? priority : MessagePriority.Normal;
    public int Attempts => _entry.DeliveryCount;
    public bool IsHandled => Volatile.Read(ref _isHandled) == 1;
    public CancellationToken CancellationToken { get; }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return Task.CompletedTask;

        return _transport.CompleteAsync(_entry, cancellationToken);
    }

    public Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return Task.CompletedTask;

        return _transport.AbandonAsync(_entry, cancellationToken);
    }

    internal async Task AbandonAsync(TimeSpan redeliveryDelay, CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return;

        if (_transport is ISupportsRedeliveryDelay redelivery)
        {
            await redelivery.AbandonAsync(_entry, redeliveryDelay, cancellationToken).AnyContext();
            return;
        }

        if (_runtimeStore is null)
            throw new MessageQueueException($"Delayed redelivery requires either native redelivery-delay support from transport \"{_transport.GetType().Name}\" or {nameof(QueueOptions)}.{nameof(QueueOptions.RuntimeStore)}.");

        int nextAttempt = _entry.DeliveryCount + 1;
        var headers = _entry.Headers.ToBuilder()
            .Set(KnownHeaders.Attempts, nextAttempt.ToString(CultureInfo.InvariantCulture))
            .Build();

        await _runtimeStore.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = $"{_entry.Id}:retry:{nextAttempt}",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = _entry.Destination,
            Body = _entry.Body,
            Headers = headers,
            Options = new TransportSendOptions { Priority = Priority },
            DueUtc = _timeProvider.GetUtcNow().Add(redeliveryDelay)
        }, cancellationToken).AnyContext();

        await _transport.CompleteAsync(_entry, cancellationToken).AnyContext();
    }

    public async Task DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return;

        await DeadLetterAsync(_transport, _entry, reason, cancellationToken).AnyContext();
    }

    public Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        return _transport is ISupportsLockRenewal lockRenewal
            ? lockRenewal.RenewLockAsync(_entry, duration, cancellationToken)
            : throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support lock renewal.");
    }

    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Message progress reporting requires tracked job execution and is not available for untracked queue or pub/sub messages.");
    }

    internal static async Task DeadLetterAsync(IMessageTransport transport, TransportEntry entry, string? reason, CancellationToken cancellationToken)
    {
        if (transport is not ISupportsDeadLetter deadLetter)
            throw new NotSupportedException($"Transport \"{transport.GetType().Name}\" does not support dead-lettering.");

        await deadLetter.DeadLetterAsync(entry, reason, cancellationToken).AnyContext();
    }

    private bool TryMarkHandled()
    {
        return Interlocked.CompareExchange(ref _isHandled, 1, 0) == 0;
    }
}

internal static class MessageRoutingConventions
{
    public static string ToKebabCase(string value)
    {
        if (String.IsNullOrEmpty(value))
            return value;

        Span<char> buffer = stackalloc char[value.Length * 2];
        int position = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (Char.IsUpper(current))
            {
                if (index > 0)
                    buffer[position++] = '-';

                buffer[position++] = Char.ToLowerInvariant(current);
            }
            else
            {
                buffer[position++] = current;
            }
        }

        return new String(buffer[..position]);
    }
}

internal sealed class MessageConsumerHandle : IMessageConsumer
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Action<string, MessageConsumerHandle> _remove;
    private IPushSubscription? _pushSubscription;
    private Task? _worker;
    private int _isDisposed;

    public MessageConsumerHandle(string source, string key, Action<string, MessageConsumerHandle> remove)
    {
        Source = source;
        Key = key;
        _remove = remove;
    }

    public string Source { get; }
    public string Key { get; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;
    public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

    public void SetPushSubscription(IPushSubscription subscription)
    {
        _pushSubscription = subscription;
    }

    public void Start(Task worker)
    {
        _worker = worker;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        await _cancellationTokenSource.CancelAsync().AnyContext();

        if (_pushSubscription is not null)
            await _pushSubscription.DisposeAsync().AnyContext();

        if (_worker is not null)
        {
            try
            {
                await _worker.AnyContext();
            }
            catch (OperationCanceledException) { }
        }

        _cancellationTokenSource.Dispose();
        _remove(Key, this);
    }
}
