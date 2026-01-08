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

## Next Steps

- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
