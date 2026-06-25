using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Queues;

public enum AckMode
{
    Auto,
    Manual
}

public sealed record EnqueueOptions
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

public sealed record ReceiveOptions
{
    public string? Source { get; init; }
    public TimeSpan? MaxWaitTime { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record WorkerOptions
{
    public AckMode AckMode { get; init; } = AckMode.Auto;
    public string? Source { get; init; }
    public int MaxConcurrency { get; init; } = 1;
    public int MaxAttempts { get; init; } = 5;
    public Func<int, TimeSpan>? RedeliveryBackoff { get; init; }
}

public sealed record MessageQueueOptions
{
    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    public string ContentType { get; init; } = "application/json";
    public Func<Type, string>? DestinationResolver { get; init; }
    public Func<Type, string>? MessageTypeResolver { get; init; }
}

public interface IMessageQueue : IAsyncDisposable
{
    Task<string> EnqueueAsync<T>(T message, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task EnqueueBatchAsync<T>(IEnumerable<T> messages, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IReceivedMessage<T>?> ReceiveAsync<T>(ReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task StartWorkingAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, WorkerOptions? options = null, CancellationToken cancellationToken = default) where T : class;
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
    Task RejectAsync(bool retry = true, string? reason = null, CancellationToken cancellationToken = default);
    Task DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default);
    Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default);
    Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default);
}

public sealed class MessageQueue : IMessageQueue
{
    private readonly IMessageTransport _transport;
    private readonly MessageQueueOptions _options;
    private int _isDisposed;

    public MessageQueue(IMessageTransport transport, MessageQueueOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new MessageQueueOptions();
    }

    public async Task<string> EnqueueAsync<T>(T message, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        options ??= new EnqueueOptions();
        string destination = GetDestination(typeof(T), options.Destination);
        var result = await _transport.SendAsync(destination, [CreateTransportMessage(message, options)], CreateSendOptions(options), cancellationToken).AnyContext();
        var item = result.Items.Count > 0 ? result.Items[0] : null;

        if (item is null || !item.Success)
            throw new QueueException($"Unable to enqueue message to \"{destination}\": {item?.ErrorCode ?? "unknown error"}");

        return item.MessageId ?? throw new QueueException($"Transport did not return a message id for \"{destination}\".");
    }

    public async Task EnqueueBatchAsync<T>(IEnumerable<T> messages, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        options ??= new EnqueueOptions();
        string destination = GetDestination(typeof(T), options.Destination);
        var transportMessages = messages.Select(message =>
        {
            ArgumentNullException.ThrowIfNull(message);
            return CreateTransportMessage(message, options);
        }).ToArray();

        if (transportMessages.Length == 0)
            return;

        var result = await _transport.SendAsync(destination, transportMessages, CreateSendOptions(options), cancellationToken).AnyContext();
        if (!result.AllSucceeded)
            throw new QueueException($"Unable to enqueue {result.Items.Count(i => !i.Success)} of {result.Items.Count} messages to \"{destination}\".");
    }

    public async Task<IReceivedMessage<T>?> ReceiveAsync<T>(ReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ThrowIfDisposed();

        if (_transport is not ISupportsPull pull)
            throw new QueueException($"Transport \"{_transport.GetType().Name}\" does not support pull receive.");

        options ??= new ReceiveOptions();
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

    public async Task StartWorkingAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, WorkerOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        options ??= new WorkerOptions();
        string source = GetDestination(typeof(T), options.Source);

        if (_transport is ISupportsPush push)
        {
            await using var subscription = await push.SubscribeAsync(source, async (entry, token) =>
            {
                var received = await CreateReceivedMessageAsync<T>(entry, token).AnyContext();
                await HandleMessageAsync(received, handler, options, token).AnyContext();
            }, new PushOptions { MaxConcurrentMessages = Math.Max(1, options.MaxConcurrency) }, cancellationToken).AnyContext();

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).AnyContext();
            return;
        }

        if (_transport is not ISupportsPull pull)
            throw new QueueException($"Transport \"{_transport.GetType().Name}\" does not support receiving messages.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var entries = await pull.ReceiveAsync(source, new ReceiveRequest
            {
                MaxMessages = Math.Max(1, options.MaxConcurrency),
                MaxWaitTime = TimeSpan.FromSeconds(1)
            }, cancellationToken).AnyContext();

            foreach (var entry in entries)
            {
                var received = await CreateReceivedMessageAsync<T>(entry, cancellationToken).AnyContext();
                await HandleMessageAsync(received, handler, options, cancellationToken).AnyContext();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return ValueTask.CompletedTask;

        return _transport.DisposeAsync();
    }

    private async Task<IReceivedMessage<T>> CreateReceivedMessageAsync<T>(TransportEntry entry, CancellationToken ct) where T : class
    {
        try
        {
            var message = _options.Serializer.Deserialize<T>(entry.Body);
            if (message is null)
                throw new QueueException($"Message \"{entry.Id}\" deserialized to null.");

            return new ReceivedMessage<T>(_transport, entry, message, ct);
        }
        catch (Exception ex) when (ex is not QueueException)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", ct).AnyContext();
            throw new QueueException($"Unable to deserialize message \"{entry.Id}\".", ex);
        }
        catch (QueueException)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", ct).AnyContext();
            throw;
        }
    }

    private async Task HandleMessageAsync<T>(IReceivedMessage<T> message, Func<IReceivedMessage<T>, CancellationToken, Task> handler, WorkerOptions options, CancellationToken ct) where T : class
    {
        try
        {
            await handler(message, ct).AnyContext();

            if (options.AckMode == AckMode.Auto && !message.IsHandled)
                await message.CompleteAsync(ct).AnyContext();
        }
        catch
        {
            if (!message.IsHandled)
                await message.RejectAsync(message.Attempts < options.MaxAttempts, "handler-error", ct).AnyContext();
        }
    }

    private TransportMessage CreateTransportMessage<T>(T message, EnqueueOptions options) where T : class
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
            headers.Set(KnownHeaders.Expiration, DateTimeOffset.UtcNow.Add(ttl).ToString("O", CultureInfo.InvariantCulture));

        return new TransportMessage
        {
            Body = _options.Serializer.SerializeToBytes(message),
            Headers = headers.Build()
        };
    }

    private static TransportSendOptions CreateSendOptions(EnqueueOptions options)
    {
        return new TransportSendOptions
        {
            Priority = options.Priority,
            DeliverAt = options.DeliverAt ?? (options.Delay is { } delay ? DateTimeOffset.UtcNow.Add(delay) : null),
            DeduplicationId = options.DeduplicationId
        };
    }

    private string GetDestination(Type messageType, string? destination)
    {
        return !String.IsNullOrEmpty(destination)
            ? destination
            : (_options.DestinationResolver?.Invoke(messageType) ?? ToKebabCase(messageType.Name));
    }

    private string GetMessageType(Type messageType)
    {
        return _options.MessageTypeResolver?.Invoke(messageType) ?? messageType.FullName ?? messageType.Name;
    }

    private async Task DeadLetterPoisonMessageAsync(TransportEntry entry, string reason, CancellationToken ct)
    {
        if (_transport is ISupportsDeadLetter deadLetter)
            await deadLetter.DeadLetterAsync(entry, reason, ct).AnyContext();
        else
            await _transport.AbandonAsync(entry, ct).AnyContext();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
    }

    private static string ToKebabCase(string value)
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

internal sealed class ReceivedMessage<T> : IReceivedMessage<T> where T : class
{
    private readonly IMessageTransport _transport;
    private readonly TransportEntry _entry;
    private int _isHandled;

    public ReceivedMessage(IMessageTransport transport, TransportEntry entry, T message, CancellationToken cancellationToken)
    {
        _transport = transport;
        _entry = entry;
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

    public Task RejectAsync(bool retry = true, string? reason = null, CancellationToken cancellationToken = default)
    {
        return retry ? AbandonAsync(cancellationToken) : DeadLetterAsync(reason, cancellationToken);
    }

    public async Task DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return;

        if (_transport is ISupportsDeadLetter deadLetter)
            await deadLetter.DeadLetterAsync(_entry, reason, cancellationToken).AnyContext();
        else
            await _transport.AbandonAsync(_entry, cancellationToken).AnyContext();
    }

    public Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        return _transport is ISupportsLockRenewal lockRenewal
            ? lockRenewal.RenewLockAsync(_entry, duration, cancellationToken)
            : Task.CompletedTask;
    }

    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private Task AbandonAsync(CancellationToken ct)
    {
        if (!TryMarkHandled())
            return Task.CompletedTask;

        return _transport.AbandonAsync(_entry, ct);
    }

    private bool TryMarkHandled()
    {
        return Interlocked.CompareExchange(ref _isHandled, 1, 0) == 0;
    }
}
