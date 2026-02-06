# Foundatio.AWS

Foundatio provides AWS implementations for file storage, queuing, and messaging using Amazon S3, Amazon SQS, and Amazon SNS. [View source on GitHub →](https://github.com/FoundatioFx/Foundatio.AWS)

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `S3FileStorage` | `IFileStorage` | Foundatio.AWS |
| `SQSMessageBus` | `IMessageBus` | Foundatio.AWS |
| `SQSQueue<T>` | `IQueue<T>` | Foundatio.AWS |

## Installation

```bash
dotnet add package Foundatio.AWS
```

## S3FileStorage

Store files in Amazon S3 with full support for buckets, prefixes, and metadata.

```csharp
using Foundatio.Storage;

var storage = new S3FileStorage(o =>
{
    o.ConnectionString = connectionString;
    // Or: o.Bucket = "my-files"; o.Region = RegionEndpoint.USEast1;
});

await storage.SaveFileAsync("documents/report.pdf", pdfStream);
```

### Configuration

| Option | Type | Required | Description |
|--------|------|----------|-------------|
| `Bucket` | `string` | ✅ | S3 bucket name |
| `Region` | `RegionEndpoint` | ✅ | AWS region |
| `ConnectionString` | `string` | | Parses all settings |
| `Credentials` | `AWSCredentials` | | AWS credentials |
| `ServiceUrl` | `string` | | Custom endpoint (LocalStack) |

For additional options, see [S3FileStorageOptions source](https://github.com/FoundatioFx/Foundatio.AWS/blob/main/src/Foundatio.AWS/Storage/S3FileStorageOptions.cs).

## SQSMessageBus

AWS SNS/SQS message bus for pub/sub messaging using the SNS fan-out pattern.

```csharp
using Foundatio.Messaging;

var messageBus = new SQSMessageBus(o =>
{
    o.ConnectionString = connectionString;
    o.Topic = "events";
    // Optional: Specify queue name for durable subscriptions
    // o.SubscriptionQueueName = "my-service-queue";
});

await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    Console.WriteLine($"Order created: {order.OrderId}");
});

await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

### Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `Topic` | `string` | ✅ | | SNS topic name for publishing |
| `ConnectionString` | `string` | | | Connection string |
| `Credentials` | `AWSCredentials` | | | AWS credentials |
| `Region` | `RegionEndpoint` | | | AWS region |
| `ServiceUrl` | `string` | | | Custom endpoint (LocalStack) |
| `CanCreateTopic` | `bool` | | `true` | Auto-create SNS topic if missing |
| `SubscriptionQueueName` | `string` | | Random | SQS queue name (use for durable subscriptions) |
| `SubscriptionQueueAutoDelete` | `bool` | | `true` | Auto-delete queue on dispose (set `false` for durable) |
| `ReadQueueTimeout` | `TimeSpan` | | 20s | Long polling timeout |
| `DequeueInterval` | `TimeSpan` | | 1s | Interval between dequeue attempts |
| `MessageVisibilityTimeout` | `TimeSpan?` | | 30s (SQS) | Message visibility timeout |
| `SqsManagedSseEnabled` | `bool` | | `false` | Enable SQS managed encryption (SSE-SQS) |
| `KmsMasterKeyId` | `string` | | | KMS key ID for encryption (SSE-KMS) |
| `KmsDataKeyReusePeriodSeconds` | `int` | | 300 | KMS key reuse period |
| `TopicResolver` | `Func<Type, string>` | | | Route message types to different topics |

For additional options, see [SQSMessageBusOptions source](https://github.com/FoundatioFx/Foundatio.AWS/blob/main/src/Foundatio.AWS/Messaging/SQSMessageBusOptions.cs).

### Architecture

The `SQSMessageBus` uses the SNS fan-out pattern:

- **Publishing**: Messages are published to an SNS topic
- **Subscribing**: Each subscriber gets its own SQS queue subscribed to the SNS topic
- **Durable Subscriptions**: Use `SubscriptionQueueName` and set `SubscriptionQueueAutoDelete = false` to persist queues across restarts
- **Policy Management**: Queue policies are automatically configured to allow SNS to deliver messages

### Durable Subscriptions Example

```csharp
var messageBus = new SQSMessageBus(o =>
{
    o.ConnectionString = connectionString;
    o.Topic = "events";
    o.SubscriptionQueueName = "order-service-events";
    o.SubscriptionQueueAutoDelete = false; // Queue persists across restarts
});
```

## SQSQueue

AWS SQS queue implementation for reliable work item processing.

```csharp
using Foundatio.Queues;

var queue = new SQSQueue<WorkItem>(o =>
{
    o.ConnectionString = connectionString;
    o.Name = "work-items";
});

await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();
```

### Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `Name` | `string` | ✅ | | Queue name |
| `ConnectionString` | `string` | | | Connection string |
| `Region` | `RegionEndpoint` | | | AWS region |
| `CanCreateQueue` | `bool` | | `true` | Auto-create queue |
| `SupportDeadLetter` | `bool` | | `true` | Enable DLQ support |
| `ReadQueueTimeout` | `TimeSpan` | | 20s | Long polling timeout |

For additional options, see [SQSQueueOptions source](https://github.com/FoundatioFx/Foundatio.AWS/blob/main/src/Foundatio.AWS/Queues/SQSQueueOptions.cs).

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns
- [Queues Guide](/guide/queues) - Queue processing patterns
- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
