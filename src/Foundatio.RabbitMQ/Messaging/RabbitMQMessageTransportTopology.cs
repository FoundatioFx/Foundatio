using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Declares the exchanges, queues and bindings the transport uses, once per transport, on a channel of its own: a
/// declare the broker refuses closes the channel it was sent on. Every declaration uses the same arguments wherever it
/// is made, because RabbitMQ refuses to declare an existing entity with different ones.
/// </summary>
internal sealed class RabbitMQMessageTransportTopology : IAsyncDisposable
{
    private static readonly TimeSpan DeclareTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Durable sources are quorum queues without a delivery limit: RabbitMQ 4 dead-letters (or drops) a message after 20
    /// deliveries by default, and Foundatio decides when a message is dead-lettered.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, object?> QueueArguments = new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum",
        ["x-delivery-limit"] = -1
    };

    private readonly RabbitMQMessageTransportNames _names;
    private readonly bool _delays;
    private readonly ILogger _logger;
    private readonly RabbitMQChannelSlot _channel;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ensured = new(StringComparer.Ordinal);

    public RabbitMQMessageTransportTopology(RabbitMQMessageTransportNames names, bool delays, Func<CancellationToken, Task<IChannel>> openChannel, ILogger logger)
    {
        _names = names;
        _delays = delays;
        _logger = logger;
        _channel = new RabbitMQChannelSlot(openChannel);
    }

    /// <summary>A durable queue for a queue, subscription or dead-letter source, bound to the delay delivery exchange.</summary>
    public Task EnsureQueueAsync(string queue, CancellationToken cancellationToken)
    {
        return EnsureAsync($"queue:{queue}", async token =>
        {
            if (_delays)
                await EnsureDelaysAsync(token).AnyContext();

            await RunAsync($"declare queue {queue}", async (channel, ct) => _ = await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>(QueueArguments), cancellationToken: ct).AnyContext(), token).AnyContext();
            if (_delays)
                await BindForDelaysAsync(queue, isExchange: false, token).AnyContext();
        }, cancellationToken);
    }

    /// <summary>A topic's fanout exchange. Messages nobody is subscribed to go to the discard queue.</summary>
    public Task EnsureTopicAsync(string exchange, CancellationToken cancellationToken)
    {
        return EnsureAsync($"topic:{exchange}", async token =>
        {
            await EnsureDiscardAsync(token).AnyContext();
            if (_delays)
                await EnsureDelaysAsync(token).AnyContext();

            await RunAsync($"declare exchange {exchange}", (channel, ct) => channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["alternate-exchange"] = _names.Discard }, cancellationToken: ct), token).AnyContext();
            if (_delays)
                await BindForDelaysAsync(exchange, isExchange: true, token).AnyContext();
        }, cancellationToken);
    }

    /// <summary>A durable subscription's queue, bound to its topic's exchange.</summary>
    public Task EnsureSubscriptionAsync(string topicExchange, string queue, CancellationToken cancellationToken)
    {
        return EnsureAsync($"subscription:{queue}", async token =>
        {
            await EnsureTopicAsync(topicExchange, token).AnyContext();
            await EnsureQueueAsync(queue, token).AnyContext();
            await RunAsync($"bind {queue} to {topicExchange}", (channel, ct) => channel.QueueBindAsync(queue, topicExchange, String.Empty, cancellationToken: ct), token).AnyContext();
        }, cancellationToken);
    }

    /// <summary>The delay levels and the exchange that delivers delayed messages to their targets (see <see cref="RabbitMQDelayLevels"/>).</summary>
    public Task EnsureDelaysAsync(CancellationToken cancellationToken)
    {
        if (!_delays)
            throw new InvalidOperationException("Delayed delivery is turned off for this transport.");

        return EnsureAsync("delays", async token =>
        {
            await EnsureDiscardAsync(token).AnyContext();
            string delivery = _names.DelayDelivery;
            await RunAsync($"declare exchange {delivery}", (channel, ct) => channel.ExchangeDeclareAsync(delivery, ExchangeType.Topic, durable: true, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["alternate-exchange"] = _names.Discard }, cancellationToken: ct), token).AnyContext();

            for (int level = 0; level < RabbitMQDelayLevels.Count; level++)
            {
                string name = _names.DelayLevel(level);
                string next = level == 0 ? delivery : _names.DelayLevel(level - 1);
                var arguments = new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-message-ttl"] = RabbitMQDelayLevels.TimeToLiveMilliseconds(level),
                    ["x-dead-letter-exchange"] = next,
                    // Keeps an expired message until the next level has confirmed it, so a delay survives a crash.
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish"
                };

                await RunAsync($"declare exchange {name}", (channel, ct) => channel.ExchangeDeclareAsync(name, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct), token).AnyContext();
                await RunAsync($"declare delay queue {name}", async (channel, ct) => _ = await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: ct).AnyContext(), token).AnyContext();
            }

            for (int level = 0; level < RabbitMQDelayLevels.Count; level++)
            {
                string name = _names.DelayLevel(level);
                string next = level == 0 ? delivery : _names.DelayLevel(level - 1);
                await RunAsync($"bind delay level {name}", async (channel, ct) =>
                {
                    await channel.QueueBindAsync(name, name, RabbitMQDelayLevels.LevelBindingKey(level, set: true), cancellationToken: ct).AnyContext();
                    await channel.ExchangeBindAsync(next, name, RabbitMQDelayLevels.LevelBindingKey(level, set: false), cancellationToken: ct).AnyContext();
                }, token).AnyContext();
            }

            _logger.LogDebug("Declared {Count} delay levels under {Prefix}", RabbitMQDelayLevels.Count, delivery);
        }, cancellationToken);
    }

    /// <summary>Removes a queue with its messages, consumers and bindings. Succeeds when it does not exist.</summary>
    public async Task DeleteQueueAsync(string queue, CancellationToken cancellationToken)
    {
        Forget(queue);
        await RunAsync($"delete queue {queue}", async (channel, ct) => _ = await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, cancellationToken: ct).AnyContext(), cancellationToken).AnyContext();
        Forget(queue);
    }

    /// <summary>Declares a queue or subscription again when it is next used, for example after another process deleted it.</summary>
    public void Forget(string queue)
    {
        _ensured.TryRemove($"queue:{queue}", out _);
        _ensured.TryRemove($"subscription:{queue}", out _);
    }

    /// <summary>Declares a topic's exchange again when it is next used.</summary>
    public void ForgetTopic(string exchange) => _ensured.TryRemove($"topic:{exchange}", out _);

    /// <summary>Declares every exchange again when it is next used.</summary>
    public void ForgetExchanges()
    {
        foreach (string key in _ensured.Keys)
        {
            if (!key.StartsWith("queue:", StringComparison.Ordinal))
                _ensured.TryRemove(key, out _);
        }
    }

    public ValueTask DisposeAsync() => _channel.DisposeAsync();

    /// <summary>Binds a queue or exchange to the delay delivery exchange by its name, so delayed messages for it arrive.</summary>
    private Task BindForDelaysAsync(string target, bool isExchange, CancellationToken cancellationToken)
    {
        string key = RabbitMQDelayLevels.DeliveryBindingKey(target);
        return RunAsync($"bind {target} to {_names.DelayDelivery}", (channel, ct) => isExchange
            ? channel.ExchangeBindAsync(target, _names.DelayDelivery, key, cancellationToken: ct)
            : channel.QueueBindAsync(target, _names.DelayDelivery, key, cancellationToken: ct), cancellationToken);
    }

    private Task EnsureDiscardAsync(CancellationToken cancellationToken)
    {
        return EnsureAsync("discard", async token =>
        {
            string discard = _names.Discard;
            await RunAsync($"declare exchange {discard}", (channel, ct) => channel.ExchangeDeclareAsync(discard, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct), token).AnyContext();
            await RunAsync($"declare queue {discard}", async (channel, ct) => _ = await channel.QueueDeclareAsync(discard, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-max-length"] = 0 }, cancellationToken: ct).AnyContext(), token).AnyContext();
            await RunAsync($"bind {discard}", (channel, ct) => channel.QueueBindAsync(discard, discard, String.Empty, cancellationToken: ct), token).AnyContext();
        }, cancellationToken);
    }

    private async Task EnsureAsync(string key, Func<CancellationToken, Task> declare, CancellationToken cancellationToken)
    {
        var lazy = _ensured.GetOrAdd(key, _ => new Lazy<Task>(async () =>
        {
            // Shared by every caller, so it is not bound to the first one's cancellation.
            using var timeout = new CancellationTokenSource(DeclareTimeout);
            await declare(timeout.Token).AnyContext();
        }));

        try
        {
            await lazy.Value.WaitAsync(cancellationToken).AnyContext();
        }
        catch (Exception) when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _ensured.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
            throw;
        }
    }

    private async Task RunAsync(string what, Func<IChannel, CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        var channel = await _channel.GetAsync(cancellationToken).AnyContext();
        try
        {
            await operation(channel, cancellationToken).AnyContext();
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason is { ReplyCode: 406 } reason)
        {
            throw new MessageBusException($"RabbitMQ refused to {what} because it exists with different arguments: {reason.ReplyText}. " +
                "Delete it so the transport can declare it again, or use another Prefix.", ex);
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason is { ReplyCode: 403 } reason)
        {
            throw new MessageBusException($"RabbitMQ refused to {what}: {reason.ReplyText}.", ex);
        }
        catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException or BrokerUnreachableException)
        {
            throw new MessageBusException($"Could not {what}: {ex.Message}", ex);
        }
    }
}
