using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

public class InMemoryMessageBus : MessageBusBase<InMemoryMessageBusOptions>
{
    private readonly ConcurrentDictionary<string, long> _messageCounts = new();
    private long _messagesSent;

    public InMemoryMessageBus() : this(o => o) { }

    public InMemoryMessageBus(InMemoryMessageBusOptions options) : base(options) { }

    public InMemoryMessageBus(Builder<InMemoryMessageBusOptionsBuilder, InMemoryMessageBusOptions> config)
        : this(config(new InMemoryMessageBusOptionsBuilder()).Build()) { }

    public long MessagesSent => _messagesSent;

    public long GetMessagesSent(Type messageType)
    {
        return _messageCounts.TryGetValue(GetMappedMessageType(messageType), out long count) ? count : 0;
    }

    public long GetMessagesSent<T>()
    {
        return _messageCounts.TryGetValue(GetMappedMessageType(typeof(T)), out long count) ? count : 0;
    }

    public void ResetMessagesSent()
    {
        Interlocked.Exchange(ref _messagesSent, 0);
        _messageCounts.Clear();
    }

    protected override async Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _messagesSent);
        _messageCounts.AddOrUpdate(messageType, t => 1, (t, c) => c + 1);
        var mappedType = GetMappedMessageType(messageType);

        if (_subscribers.IsEmpty)
            return;

        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
            SendDelayedMessage(mappedType, message, options.DeliveryDelay.Value);
            return;
        }

        byte[] body = SerializeMessageBody(messageType, message);
        var messageData = new Message(body, DeserializeMessageBody)
        {
            CorrelationId = options.CorrelationId,
            UniqueId = options.UniqueId,
            Type = messageType,
            ClrType = mappedType
        };

        foreach (var property in options.Properties)
            messageData.Properties[property.Key] = property.Value;

        try
        {
            await SendMessageToSubscribersAsync(messageData).AnyContext();
        }
        catch (Exception ex)
        {
            // swallow exceptions from subscriber handlers for the in memory bus
            _logger.LogWarning(ex, "Error sending message to subscribers: {Message}", ex.Message);
        }
    }
}
