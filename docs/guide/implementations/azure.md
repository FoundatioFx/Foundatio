# Foundatio.AzureStorage / Foundatio.AzureServiceBus

Foundatio provides Azure implementations for storage, queuing, and messaging using Azure Blob Storage, Azure Storage Queues, and Azure Service Bus. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.AzureStorage) | [AzureServiceBus](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AzureServiceBus)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `AzureFileStorage` | `IFileStorage` | Foundatio.AzureStorage |
| `AzureStorageQueue<T>` | `IQueue<T>` | Foundatio.AzureStorage |
| `AzureServiceBusQueue<T>` | `IQueue<T>` | Foundatio.AzureServiceBus |
| `AzureServiceBusMessageBus` | `IMessageBus` | Foundatio.AzureServiceBus |
| `AzureServiceBusMessageTransport` | `IMessageTransport` ([Messages and Events](/guide/messages)) | Foundatio.AzureServiceBus |

## Installation

```bash
# Azure Storage (Blob, Storage Queues)
dotnet add package Foundatio.AzureStorage

# Azure Service Bus (Queues, Messaging)
dotnet add package Foundatio.AzureServiceBus
```

## AzureFileStorage

Azure Blob Storage file storage.

```csharp
using Foundatio.Storage;

var storage = new AzureFileStorage(o =>
{
    o.ConnectionString = connectionString;
    o.ContainerName = "files";
});

await storage.SaveFileAsync("documents/report.pdf", pdfStream);
```

### Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `ConnectionString` | `string` | ✅ | | Azure Storage connection string |
| `ContainerName` | `string` | | `"storage"` | Blob container name |

For additional options, see [AzureFileStorageOptions source](https://github.com/FoundatioFx/Foundatio.AzureStorage/blob/main/src/Foundatio.AzureStorage/Storage/AzureFileStorageOptions.cs).

## AzureStorageQueue

Azure Storage Queue implementation.

```csharp
using Foundatio.Queues;

var queue = new AzureStorageQueue<WorkItem>(o =>
{
    o.ConnectionString = connectionString;
    o.Name = "work-items";
});

await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
```

### Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `ConnectionString` | `string` | ✅ | | Azure Storage connection string |
| `DequeueInterval` | `TimeSpan` | | 2s | Polling interval |

For additional options, see [AzureStorageQueueOptions source](https://github.com/FoundatioFx/Foundatio.AzureStorage/blob/main/src/Foundatio.AzureStorage/Queues/AzureStorageQueueOptions.cs).

## AzureServiceBusQueue

Azure Service Bus queue with advanced features.

```csharp
using Foundatio.Queues;

var queue = new AzureServiceBusQueue<WorkItem>(o =>
{
    o.ConnectionString = connectionString;
    o.Name = "work-items";
});

await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
```

### Configuration

| Option | Type | Required | Description |
|--------|------|----------|-------------|
| `ConnectionString` | `string` | ✅ | Service Bus connection string |
| `RequiresSession` | `bool?` | | Create the queue with sessions enabled. Each message then needs a `GroupId` (sent as `SessionId`), and messages are processed one at a time, in order, per group. `RetryDelay` is not applied, so an abandoned message is retried immediately ahead of later messages in its group |
| `RequiresDuplicateDetection` | `bool?` | | Enable duplicate detection |
| `EnableDeadLetteringOnMessageExpiration` | `bool?` | | DLQ on expiration |

For additional options, see [AzureServiceBusQueueOptions source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio.AzureServiceBus/Queues/AzureServiceBusQueueOptions.cs).

## AzureServiceBusMessageBus

Azure Service Bus pub/sub messaging.

```csharp
using Foundatio.Messaging;

var messageBus = new AzureServiceBusMessageBus(o =>
{
    o.ConnectionString = connectionString;
    o.Topic = "events";
    o.SubscriptionName = "my-service";
});

await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    Console.WriteLine($"Order created: {order.OrderId}");
});

await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

### Configuration

| Option | Type | Required | Description |
|--------|------|----------|-------------|
| `ConnectionString` | `string` | ✅ | Service Bus connection string |
| `SubscriptionName` | `string` | | Subscription name (unique per consumer) |
| `PrefetchCount` | `int?` | | Message prefetch count |

For additional options, see [AzureServiceBusMessageBusOptions source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio.AzureServiceBus/Messaging/AzureServiceBusMessageBusOptions.cs).

## Messages and Events

`AzureServiceBusMessageTransport` runs [Messages and Events](/guide/messages) on Service Bus queues, topics and subscriptions. Register it with `UseAzureServiceBus`:

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseAzureServiceBus(new AzureServiceBusMessageTransportOptions { ConnectionString = connectionString, Prefix = "prod-" })
    .AddConsumer<SendReceiptHandler>());
```

- Queues are named `{Prefix}queue.{name}` and topics `{Prefix}topic.{name}`, since Service Bus queues and topics share one namespace; durable subscriptions are named after the service, and instance subscriptions are created per receiver and removed by Service Bus once idle.
- Leases are peek-lock locks with the entity's lock duration (`LockDuration`, 1 minute by default), renewed by Foundatio. Delays are scheduled messages; a delayed retry is a scheduled copy.
- `RequiresSession = true` orders groups with sessions; `RequiresDuplicateDetection = true` drops repeated deduplication ids.
- `CreateEntities = false` uses entities created ahead of time, for applications without Manage rights.

See [Using Azure Service Bus](/guide/messages#using-azure-service-bus) and the [package README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AzureServiceBus#message-transport-preview) for the mapping, sessions, sizes and limits.

### Monitoring Messages

The transport's `GetStatsAsync` reads the queue's or subscription's runtime properties, one administration call.

- **Queued**: `ActiveMessageCount`. Service Bus counts locked messages as active, so messages being handled count here.
- **Delayed**: a queue's `ScheduledMessageCount`, delayed sends and retries. Null for subscriptions, whose scheduled messages wait in the topic.
- **InFlight**: null.
- **DeadLettered**: `DeadLetterMessageCount`.

Instance subscriptions are not counted (null), and a source that does not exist counts as empty. The emulator reports zero for every count, so on it the transport returns null.

## Running the Tests

The Azure Service Bus provider lives in the [Foundatio repository](https://github.com/FoundatioFx/Foundatio) under `src/Foundatio.AzureServiceBus`, with its tests in `tests/Foundatio.AzureServiceBus.Tests`. The tests run against the [Azure Service Bus emulator](https://learn.microsoft.com/azure/service-bus-messaging/overview-emulator) at `localhost:5672`, with the queues and topics defined in `build/servicebus-emulator/Config.json`. The message transport tests create their own entities through the emulator's management API on `localhost:5300` and delete them when each test ends, since the emulator holds at most 50 queues. Start it from the repository root and run them:

```bash
docker compose up -d servicebus-emulator
dotnet test --project tests/Foundatio.AzureServiceBus.Tests/Foundatio.AzureServiceBus.Tests.csproj
```

The tests read their connection string from `ConnectionStrings:AzureServiceBusConnectionString` (environment variable `ConnectionStrings__AzureServiceBusConnectionString`). Azure Storage and its tests live in the [Foundatio.AzureStorage](https://github.com/FoundatioFx/Foundatio.AzureStorage) repository.

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns
- [Queues Guide](/guide/queues) - Queue processing patterns
- [Messaging Guide](/guide/messaging) - Pub/sub patterns
- [Serialization](/guide/serialization) - Configure serialization
