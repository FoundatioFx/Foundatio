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

    /// <summary>
    /// A landing queue expires each message as it arrives and dead-letters it, at least once, into the exchange that
    /// delivers it to its target. It takes the place of its target as the delay levels' next hop: a target that refuses
    /// messages then holds up only its own landing queue, not a delay level every target shares.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, object?> LandingArguments = new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum",
        ["x-message-ttl"] = 0,
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish"
    };

    private readonly RabbitMQMessageTransportNames _names;
    private readonly bool _delays;
    private readonly RabbitMQDelayInspector? _inspector;
    private readonly ILogger _logger;
    private readonly RabbitMQChannelSlot _channel;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ensured = new(StringComparer.Ordinal);

    public RabbitMQMessageTransportTopology(RabbitMQMessageTransportNames names, bool delays, Func<CancellationToken, Task<IChannel>> openChannel,
        RabbitMQDelayInspector? inspector, ILogger logger)
    {
        _names = names;
        _delays = delays;
        _inspector = inspector;
        _logger = logger;
        _channel = new RabbitMQChannelSlot(openChannel);
    }

    /// <summary>A durable queue for a queue, subscription or dead-letter source, bound by its name to the exchange that delivers delayed messages.</summary>
    public Task EnsureQueueAsync(string queue, CancellationToken cancellationToken)
    {
        return EnsureAsync($"queue:{queue}", async token =>
        {
            if (_delays)
                await EnsureDelaysAsync(token).AnyContext();

            await RunAsync($"declare queue {queue}", async (channel, ct) => _ = await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>(QueueArguments), cancellationToken: ct).AnyContext(), token).AnyContext();
            if (_delays)
            {
                await BindForDelaysAsync(queue, isExchange: false, token).AnyContext();
                _inspector?.InspectTargetInBackground(queue);
            }
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

    /// <summary>
    /// The delay levels (see <see cref="RabbitMQDelayLevels"/>), the exchange they deliver to, which routes to landing
    /// queues, and the exchange that delivers to targets, its alternate exchange for targets without a landing queue.
    /// </summary>
    public Task EnsureDelaysAsync(CancellationToken cancellationToken)
    {
        if (!_delays)
            throw new InvalidOperationException("Delayed delivery is turned off for this transport.");

        return EnsureAsync("delays", async token =>
        {
            await EnsureDiscardAsync(token).AnyContext();
            string final = _names.DelayFinal;
            await RunAsync($"declare exchange {final}", (channel, ct) => channel.ExchangeDeclareAsync(final, ExchangeType.Topic, durable: true, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["alternate-exchange"] = _names.Discard }, cancellationToken: ct), token).AnyContext();
            string delivery = _names.DelayDelivery;
            await RunAsync($"declare exchange {delivery}", (channel, ct) => channel.ExchangeDeclareAsync(delivery, ExchangeType.Topic, durable: true, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["alternate-exchange"] = final }, cancellationToken: ct), token).AnyContext();

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
            _inspector?.CheckDelaysInBackground(force: true);
        }, cancellationToken);
    }

    /// <summary>
    /// The landing queue of <paramref name="target"/>, a queue or a topic's exchange, which is declared before its first
    /// delayed message and bound to the delivery exchange by the target's name. Instance subscriptions have none: their
    /// delayed retries reach them through the delivery exchange's alternate exchange, as do those of a target whose
    /// landing queue an operator deleted, until it is declared again.
    /// </summary>
    public Task EnsureLandingAsync(string target, CancellationToken cancellationToken)
    {
        return EnsureAsync($"landing:{target}", async token =>
        {
            await EnsureDelaysAsync(token).AnyContext();
            string landing = _names.Landing(target);
            var arguments = new Dictionary<string, object?>(LandingArguments) { ["x-dead-letter-exchange"] = _names.DelayFinal };
            await RunAsync($"declare landing queue {landing}", async (channel, ct) => _ = await channel.QueueDeclareAsync(landing, durable: true, exclusive: false, autoDelete: false,
                arguments: arguments, cancellationToken: ct).AnyContext(), token).AnyContext();
            await RunAsync($"bind {landing} to {_names.DelayDelivery}", (channel, ct) => channel.QueueBindAsync(landing, _names.DelayDelivery,
                RabbitMQDelayLevels.DeliveryBindingKey(target), cancellationToken: ct), token).AnyContext();
            _inspector?.InspectLandingInBackground(landing);
        }, cancellationToken);
    }

    /// <summary>Removes a queue with its messages, consumers and bindings, and its landing queue. Succeeds when they do not exist.</summary>
    public async Task DeleteQueueAsync(string queue, CancellationToken cancellationToken)
    {
        Forget(queue);
        await RunAsync($"delete queue {queue}", async (channel, ct) => _ = await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, cancellationToken: ct).AnyContext(), cancellationToken).AnyContext();
        if (_delays)
        {
            string landing = _names.Landing(queue);
            await RunAsync($"delete queue {landing}", async (channel, ct) => _ = await channel.QueueDeleteAsync(landing, ifUnused: false, ifEmpty: false, cancellationToken: ct).AnyContext(), cancellationToken).AnyContext();
        }

        Forget(queue);
    }

    /// <summary>Declares a queue or subscription, and its landing queue, again when it is next used, for example after another process deleted it.</summary>
    public void Forget(string queue)
    {
        _ensured.TryRemove($"queue:{queue}", out _);
        _ensured.TryRemove($"subscription:{queue}", out _);
        _ensured.TryRemove($"landing:{queue}", out _);
    }

    /// <summary>Declares every exchange, and every landing queue with its binding, again when it is next used.</summary>
    public void ForgetExchanges()
    {
        foreach (string key in _ensured.Keys)
        {
            if (!key.StartsWith("queue:", StringComparison.Ordinal))
                _ensured.TryRemove(key, out _);
        }
    }

    public ValueTask DisposeAsync() => _channel.DisposeAsync();

    /// <summary>Binds a queue or exchange by its name to the exchange that delivers delayed messages, so delayed messages for it arrive.</summary>
    private Task BindForDelaysAsync(string target, bool isExchange, CancellationToken cancellationToken)
    {
        string key = RabbitMQDelayLevels.DeliveryBindingKey(target);
        string final = _names.DelayFinal;
        return RunAsync($"bind {target} to {final}", (channel, ct) => isExchange
            ? channel.ExchangeBindAsync(target, final, key, cancellationToken: ct)
            : channel.QueueBindAsync(target, final, key, cancellationToken: ct), cancellationToken);
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
