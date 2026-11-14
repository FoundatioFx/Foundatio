# Foundatio.Kafka

Foundatio provides Apache Kafka messaging for high-throughput event streaming. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Kafka)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `KafkaMessageBus` | `IMessageBus` | Foundatio.Kafka |
| `KafkaMessageTransport` | `IMessageTransport` ([Messages and Events](/guide/messages)) | Foundatio.Kafka |

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

For additional options, see [KafkaMessageBusOptions source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio.Kafka/Messaging/KafkaMessageBusOptions.cs).

## Messages and Events

`KafkaMessageTransport` runs [Messages and Events](/guide/messages) on Kafka. Register it with `UseKafka`:

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseKafka("localhost:9092")
    .AddConsumer<SendReceiptHandler>());
```

See [Using Kafka](/guide/messages#using-kafka) and the [package README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Kafka#message-transport-preview) for options, topics and limits.

## Monitoring

`KafkaMessageTransport.GetStatsAsync(source)` reports a queue's or durable subscription's backlog from Kafka's consumer lag, without joining a consumer group or creating anything:

| Count | On Kafka |
| ----- | -------- |
| `Queued` | Records after the group's committed offset, summed over the partitions the source reads: a queue's topic, or a subscription's share of its topic plus its own topic. Includes messages received and not yet settled, since their offset is not committed yet. |
| `DeadLettered` | Records in the dead-letter topic after its consumer group's committed offset: what retention keeps (at least 14 days), less dead letters a replay completed. |
| `InFlight` | `null`: Kafka does not know what a receiver holds. |
| `Delayed` | `null`: delayed messages without a group key wait in delay topics that every source shares. |

The counts lag settlements by about `CommitInterval` (1 second by default). A source that does not exist counts as empty; an instance subscription returns `null`. Each call costs a round trip for the partitions' offsets and one per consumer group, and fails after 10 seconds. See the [package README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Kafka#message-transport-preview) for the details.

## Running the Tests

The Kafka provider lives in the [Foundatio repository](https://github.com/FoundatioFx/Foundatio) under `src/Foundatio.Kafka`, with its tests in `tests/Foundatio.Kafka.Tests`. The tests connect to a broker at `127.0.0.1:9092`. Start one from the repository root and run them:

```bash
docker compose up -d kafka
dotnet test --project tests/Foundatio.Kafka.Tests/Foundatio.Kafka.Tests.csproj
```

## Next Steps

- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
