# Foundatio.Kafka

Foundatio provides Apache Kafka messaging for high-throughput event streaming. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.Kafka)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `KafkaMessageBus` | `IMessageBus` | Foundatio.Kafka |

## Installation

```bash
dotnet add package Foundatio.Kafka
```

## Usage

```csharp
using Foundatio.Messaging;

var messageBus = new KafkaMessageBus(o =>
{
    o.BootstrapServers = "localhost:9092";
    o.Topic = "events";
    o.GroupId = "my-service";
});

await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    Console.WriteLine($"Order created: {order.OrderId}");
});

await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

## Configuration

| Option | Type | Required | Description |
|--------|------|----------|-------------|
| `BootstrapServers` | `string` | ✅ | Kafka broker addresses |
| `GroupId` | `string` | ✅ | Consumer group ID |
| `SecurityProtocol` | `SecurityProtocol?` | | Security protocol |
| `SaslMechanism` | `SaslMechanism?` | | SASL mechanism |
| `SaslUsername` | `string` | | SASL username |
| `SaslPassword` | `string` | | SASL password |

For additional options, see [KafkaMessageBusOptions source](https://github.com/FoundatioFx/Foundatio.Kafka/blob/main/src/Foundatio.Kafka/Messaging/KafkaMessageBusOptions.cs).

## Next Steps

- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
