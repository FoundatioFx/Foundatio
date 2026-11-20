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

### Ordered groups and partitions

A group's messages share a partition with other groups, and each partition is read by one receiver at a time, in order, so groups are isolated from each other only per partition.

- **A busy group**: when a group holds 100 messages behind its unsettled ones and doesn't drain within 100 ms, the receiver skips its later messages on that partition, leaves them uncommitted, and reads them again in order with a second consumer once the group has drained to 50. The partition's other groups go on being delivered. That consumer is created the first time a group skips and fetches nothing while no group is catching up.
- **Limits**: a partition skips at most 10,000 messages, and stops once its oldest skipped one is 100,000 offsets behind the newest it has read; a receiver skips at most 100,000 on all its partitions together. Past a partition's limits, the receiver logs a warning naming the group that skipped the most and the group that skipped the oldest message, and pauses the partition until they catch up, which holds up its other groups. Past the receiver's, no more groups start skipping, and it pauses the partitions with skipped messages the same way. A skipped message keeps about 160 bytes in memory, so 1.6 MB for a partition at its limit and 16 MB for a receiver at its own.
- **After a crash**, the partition's next owner reads again from its committed offset, the oldest unfinished message: usually the busy group's first unsettled one, which is older than its skipped ones, so more than the up to 100,000 offsets they span.
- **Partitions**: topics the transport creates get `Partitions` partitions, or the broker's `num.partitions` (1 unless the broker sets it). Set it before the topics are created, to at least the number of instances you run and of groups you expect to be busy at once; adding partitions later reorders groups. The first grouped send to a topic with one partition logs a warning.

## Monitoring

`KafkaMessageTransport.GetStatsAsync(source)` reports a queue's or durable subscription's backlog from Kafka's consumer lag, without joining a consumer group or creating anything:

| Count | On Kafka |
| ----- | -------- |
| `Queued` | Records after the group's committed offset, summed over the partitions the source reads: a queue's topic, or a subscription's share of its topic plus its own topic. Includes messages received and not yet settled, since their offset is not committed yet, and those after a busy group's skipped messages. |
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
