# Foundatio.AzureStorage / Foundatio.AzureServiceBus

Foundatio provides Azure implementations for storage, queuing, and messaging using Azure Blob Storage, Azure Storage Queues, and Azure Service Bus. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.AzureStorage) | [AzureServiceBus](https://github.com/FoundatioFx/Foundatio.AzureServiceBus)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `AzureFileStorage` | `IFileStorage` | Foundatio.AzureStorage |
| `AzureStorageQueue<T>` | `IQueue<T>` | Foundatio.AzureStorage |
| `AzureServiceBusQueue<T>` | `IQueue<T>` | Foundatio.AzureServiceBus |
| `AzureServiceBusMessageBus` | `IMessageBus` | Foundatio.AzureServiceBus |

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
| `RequiresSession` | `bool?` | | Enable sessions for ordered processing |
| `RequiresDuplicateDetection` | `bool?` | | Enable duplicate detection |
| `EnableDeadLetteringOnMessageExpiration` | `bool?` | | DLQ on expiration |

For additional options, see [AzureServiceBusQueueOptions source](https://github.com/FoundatioFx/Foundatio.AzureServiceBus/blob/main/src/Foundatio.AzureServiceBus/Queues/AzureServiceBusQueueOptions.cs).

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

For additional options, see [AzureServiceBusMessageBusOptions source](https://github.com/FoundatioFx/Foundatio.AzureServiceBus/blob/main/src/Foundatio.AzureServiceBus/Messaging/AzureServiceBusMessageBusOptions.cs).

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns
- [Queues Guide](/guide/queues) - Queue processing patterns
- [Messaging Guide](/guide/messaging) - Pub/sub patterns
- [Serialization](/guide/serialization) - Configure serialization
