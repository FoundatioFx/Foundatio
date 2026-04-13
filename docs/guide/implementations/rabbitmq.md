# Foundatio.RabbitMQ

Foundatio provides RabbitMQ messaging for pub/sub with durable delivery. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.RabbitMQ)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `RabbitMQMessageBus` | `IMessageBus` | Foundatio.RabbitMQ |

## Installation

```bash
dotnet add package Foundatio.RabbitMQ
```

## Usage

```csharp
using Foundatio.Messaging;

var messageBus = new RabbitMQMessageBus(o =>
{
    o.ConnectionString = "amqp://guest:guest@localhost:5672";
    o.Topic = "events";
});

await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    Console.WriteLine($"Order created: {order.OrderId}");
});

await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

## Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `ConnectionString` | `string` | ✅ | | RabbitMQ connection string |
| `IsDurable` | `bool` | | `true` | Durable messages |
| `DeliveryLimit` | `long` | | `2` | Max delivery attempts |
| `AcknowledgementStrategy` | `AcknowledgementStrategy` | | `FireAndForget` | Ack strategy |
| `PrefetchCount` | `ushort` | | `0` | Consumer prefetch count |

For additional options, see [RabbitMQMessageBusOptions source](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/main/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBusOptions.cs).

## Delayed message delivery

You can schedule delivery using `DeliveryDelay` on publish options (see the [messaging guide](/guide/messaging)).

**Behavior depends on the broker and the deprecated plugin:**

1. **RabbitMQ before 4.3 with the plugin installed** — If [`rabbitmq_delayed_message_exchange`](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/) is present, Foundatio uses it and logs a deprecation warning at startup. The plugin is archived and will not work on RabbitMQ 4.3 and later.
2. **RabbitMQ before 4.3 without the plugin** — Delayed sends fall back to the in-memory scheduler in `MessageBusBase`. **Not durable** across process restarts.
3. **RabbitMQ 4.3 and later** — The delayed-exchange probe is skipped (the plugin is incompatible). The same in-memory fallback applies automatically.

For durable delayed delivery on newer brokers, plan a migration (for example TTL + dead-letter exchanges or an external scheduler). See the [Foundatio.RabbitMQ README](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/main/README.md) for the full note.

## Next Steps

- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
