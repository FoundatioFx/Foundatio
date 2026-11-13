# Foundatio.RabbitMQ

Foundatio provides RabbitMQ messaging for pub/sub with durable delivery. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.RabbitMQ)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `RabbitMQMessageBus` | `IMessageBus` | Foundatio.RabbitMQ |
| `RabbitMQMessageTransport` | `IMessageTransport` ([Messages and Events](/guide/messages)) | Foundatio.RabbitMQ |

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

For additional options, see [RabbitMQMessageBusOptions source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBusOptions.cs).

## Delayed message delivery

You can schedule delivery using `DeliveryDelay` on publish options (see the [messaging guide](/guide/messaging)).

**Behavior depends on the broker and the deprecated plugin:**

1. **RabbitMQ before 4.3 with the plugin installed** — If [`rabbitmq_delayed_message_exchange`](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/) is present, Foundatio uses it and logs a deprecation warning at startup. The plugin is archived and will not work on RabbitMQ 4.3 and later.
2. **RabbitMQ before 4.3 without the plugin** — Delayed sends fall back to the in-memory scheduler in `MessageBusBase`. **Not durable** across process restarts.
3. **RabbitMQ 4.3 and later** — The delayed-exchange probe is skipped (the plugin is incompatible). The same in-memory fallback applies automatically.

For durable delayed delivery on newer brokers, use `RabbitMQMessageTransport` (below), whose delays wait in delay queues on the broker, or an external scheduler. See the [Foundatio.RabbitMQ README](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio.RabbitMQ/README.md) for the full note.

## Messages and Events

`RabbitMQMessageTransport` runs [Messages and Events](/guide/messages) on RabbitMQ 4 (tested on 4.2 and 4.3). Register it with `UseRabbitMQ`:

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseRabbitMQ("amqp://guest:guest@localhost:5672")
    .AddConsumer<SendReceiptHandler>());
```

Queues and subscriptions are quorum queues, topics are fanout exchanges, and instance subscriptions are exclusive queues the broker removes with their connection. Delays wait in 28 shared levels of delay queues on the broker, so they need no plugin and survive restarts. Leases never expire: a delivery stays with its receiver's channel until it is settled, so raise the broker's `consumer_timeout` (30 minutes by default) for handlers that run longer. Groups are not ordered and messages are not deduplicated.

See [Using RabbitMQ](/guide/messages#using-rabbitmq) and the [package README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.RabbitMQ#message-transport-preview) for options, names and limits.

## Monitoring

`RabbitMQMessageTransport.GetStatsAsync(source)` counts a queue's or durable subscription's messages without declaring anything:

| Count | On RabbitMQ |
| ----- | ----------- |
| `Queued` | The queue's ready messages. |
| `DeadLettered` | The dead-letter queue's ready messages, or every message in it with the management API. |
| `InFlight` | `null`, or with `ManagementEndpoint` set, the queue's unacknowledged messages, including those a receiver has buffered and not handed out yet. |
| `Delayed` | `null`: delayed messages and retries wait in delay queues that every source shares. |

Without the management API, the counts come from passive declares, one round trip each, and are current; messages delivered and not settled are then counted nowhere, because AMQP does not report them. With it, every count lags by up to the broker's statistics interval (5 seconds by default). A source that does not exist counts as empty; an instance subscription returns `null`.

## Running the Tests

The RabbitMQ provider lives in the [Foundatio repository](https://github.com/FoundatioFx/Foundatio) under `src/Foundatio.RabbitMQ`, with its tests in `tests/Foundatio.RabbitMQ.Tests`. The tests don't use `docker-compose.yml`: the Aspire AppHost in `tests/Foundatio.RabbitMQ.AppHost` starts their brokers (a RabbitMQ broker, one with the delayed-message plugin built from `build/rabbitmq-delayed`, and a three-node cluster), so they only need Docker running:

```bash
dotnet test --project tests/Foundatio.RabbitMQ.Tests/Foundatio.RabbitMQ.Tests.csproj
```

The message transport tests start one broker of their own through Aspire and remove it when the run ends. Set `RABBITMQ_TRANSPORT_IMAGE_TAG` (for example `4.2-management`) to test another version, or `RABBITMQ_TRANSPORT_CONNECTION_STRING` (and `RABBITMQ_TRANSPORT_MANAGEMENT_URI`) to use a broker you started:

```bash
dotnet test --project tests/Foundatio.RabbitMQ.Tests/Foundatio.RabbitMQ.Tests.csproj --filter-class "*RabbitMQMessageTransport*"
```

## Next Steps

- [Quorum Queue Migration](/guide/implementations/rabbitmq-quorum-migration) - Migrate from classic to quorum queues
- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
