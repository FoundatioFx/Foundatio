using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    public Type? RouteType { get; init; }
    public TimeSpan? MaxWaitTime { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record QueueConsumerOptions
{
    public AckMode AckMode { get; init; } = AckMode.Auto;
    public string? Source { get; init; }
    public Type? RouteType { get; init; }
    public string? Key { get; init; }
    public int MaxConcurrency { get; init; } = 1;
    public int MaxAttempts { get; init; } = 5;
    public Func<int, TimeSpan>? RedeliveryBackoff { get; init; }
}

public sealed record QueueOptions
{
    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    public string ContentType { get; init; } = "application/json";
    public IMessageRouter Router { get; init; } = DefaultMessageRouter.Instance;
    public IJobRuntimeStore? RuntimeStore { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public ILoggerFactory? LoggerFactory { get; init; }
}

public interface IQueue : IAsyncDisposable
{
    Task<string> EnqueueAsync<T>(T message, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task EnqueueBatchAsync<T>(IEnumerable<T> messages, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task EnqueueBatchAsync(IEnumerable<object> messages, QueueMessageOptions? options = null, CancellationToken cancellationToken = default);
    Task<IReceivedMessage?> ReceiveAsync(QueueReceiveOptions? options = null, CancellationToken cancellationToken = default);
    Task<IReceivedMessage<T>?> ReceiveAsync<T>(QueueReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IMessageConsumer> StartConsumerAsync(Func<IReceivedMessage, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default);
    Task<IMessageConsumer> StartConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task RunConsumerAsync(Func<IReceivedMessage, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default);
    Task RunConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class;
}

public interface IMessageConsumer : IAsyncDisposable
{
    string Source { get; }
    string Key { get; }
}

public interface IReceivedMessage
{
    string Id { get; }
    ReadOnlyMemory<byte> Body { get; }
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

public interface IReceivedMessage<out T> : IReceivedMessage where T : class
{
    T Message { get; }
}

/// <summary>
/// App-facing durable competing-consumer queue. Routing, serialization, settlement, scheduling, and the consumer loop
/// live in <see cref="MessageClientCore"/>; this type maps queue-shaped options onto that shared core.
/// </summary>
public sealed class MessageQueue : IQueue
{
    private readonly MessageClientCore _core;

    public MessageQueue(IMessageTransport transport, QueueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        options ??= new QueueOptions();
        var logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MessageQueue>();
        _core = new MessageClientCore(transport, options.Serializer, options.Router, options.RuntimeStore, options.TimeProvider, logger,
            static (message, inner) => inner is null ? new MessageQueueException(message) : new MessageQueueException(message, inner));
    }

    public Task<string> EnqueueAsync<T>(T message, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new QueueMessageOptions();
        return _core.SendAsync(ScheduledDispatchKind.QueueMessage, typeof(T), message, ToEnvelope(options), GetDestination(typeof(T), options.Destination), ensureDestination: null, cancellationToken);
    }

    public Task EnqueueBatchAsync<T>(IEnumerable<T> messages, QueueMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new QueueMessageOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.QueueMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetDestination(type, options.Destination), ensureDestination: null, cancellationToken);
    }

    public Task EnqueueBatchAsync(IEnumerable<object> messages, QueueMessageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new QueueMessageOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.QueueMessage, messages, null, ToEnvelope(options), type => GetDestination(type, options.Destination), ensureDestination: null, cancellationToken);
    }

    public Task<IReceivedMessage?> ReceiveAsync(QueueReceiveOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new QueueReceiveOptions();
        return _core.ReceiveAsync(GetDestination(options.RouteType ?? typeof(object), options.Source), options.MaxWaitTime, cancellationToken);
    }

    public Task<IReceivedMessage<T>?> ReceiveAsync<T>(QueueReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        options ??= new QueueReceiveOptions();
        return _core.ReceiveAsync<T>(GetDestination(options.RouteType ?? typeof(T), options.Source), options.MaxWaitTime, cancellationToken);
    }

    public async Task<IMessageConsumer> StartConsumerAsync(Func<IReceivedMessage, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new QueueConsumerOptions();
        return await _core.StartListenerAsync(BuildConfig(options.RouteType ?? typeof(object), options), handler, cancellationToken).AnyContext();
    }

    public async Task<IMessageConsumer> StartConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new QueueConsumerOptions();
        return await _core.StartListenerAsync(BuildConfig(options.RouteType ?? typeof(T), options), handler, cancellationToken).AnyContext();
    }

    public async Task RunConsumerAsync(Func<IReceivedMessage, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default)
    {
        await using var consumer = await StartConsumerAsync(handler, options, cancellationToken).AnyContext();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).AnyContext();
    }

    public async Task RunConsumerAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, QueueConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        await using var consumer = await StartConsumerAsync(handler, options, cancellationToken).AnyContext();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).AnyContext();
    }

    public ValueTask DisposeAsync()
    {
        return _core.DisposeAsync();
    }

    private ListenerConfig BuildConfig(Type routeType, QueueConsumerOptions options)
    {
        string source = GetDestination(routeType, options.Source);
        return new ListenerConfig
        {
            Source = source,
            Key = !String.IsNullOrEmpty(options.Key) ? options.Key : $"{source}:{routeType.FullName ?? routeType.Name}",
            MessageType = routeType,
            AckMode = options.AckMode,
            MaxConcurrency = options.MaxConcurrency,
            MaxAttempts = options.MaxAttempts,
            RedeliveryBackoff = options.RedeliveryBackoff
        };
    }

    private string GetDestination(Type messageType, string? destination)
    {
        return _core.Router.ResolveRoute(new MessageRouteContext
        {
            MessageType = messageType,
            Role = MessageRouteRole.QueueDestination,
            OperationOverride = destination
        });
    }

    private static MessageEnvelopeOptions ToEnvelope(QueueMessageOptions options)
    {
        return new MessageEnvelopeOptions
        {
            Priority = options.Priority,
            Delay = options.Delay,
            DeliverAt = options.DeliverAt,
            TimeToLive = options.TimeToLive,
            CorrelationId = options.CorrelationId,
            DeduplicationId = options.DeduplicationId,
            Headers = options.Headers
        };
    }
}
