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
| `ServiceUrl` | `string` | | Custom endpoint (e.g. Floci) |

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
| `ServiceUrl` | `string` | | | Custom endpoint (e.g. Floci) |
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

var queue = new SQSQueue<WorkItem>(o => o
    .ConnectionString(connectionString)
    .Name("work-items"));

await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();
```

### Configuration

| Option | Type | Required | Default | Description |
|--------|------|----------|---------|-------------|
| `Name` | `string` | ✅ | | Queue name (a `.fifo` suffix selects a FIFO queue) |
| `ConnectionString` | `string` | | | Connection string |
| `Region` | `RegionEndpoint` | | | AWS region |
| `Credentials` | `AWSCredentials` | | | AWS credentials |
| `ServiceUrl` | `string` | | | Custom endpoint (e.g. Floci) |
| `CanCreateQueue` | `bool` | | `true` | Auto-create queue |
| `SupportDeadLetter` | `bool` | | `true` | Create and use a `-deadletter` queue |
| `ReadQueueTimeout` | `TimeSpan` | | 20s | Long polling timeout |
| `DequeueInterval` | `TimeSpan` | | 1s | Interval between dequeue attempts when the queue is empty |
| `RetryDelay` | `Func<int, TimeSpan>` | | Exponential | Visibility delay before an abandoned message is retried |
| `SqsManagedSseEnabled` | `bool` | | `false` | Enable SQS managed encryption (SSE-SQS) |
| `KmsMasterKeyId` | `string` | | | KMS key ID for encryption (SSE-KMS) |
| `KmsDataKeyReusePeriodSeconds` | `int` | | 300 | KMS key reuse period |

For additional options, see [SQSQueueOptions source](https://github.com/FoundatioFx/Foundatio.AWS/blob/main/src/Foundatio.AWS/Queues/SQSQueueOptions.cs).

### Fair Queues and Message Groups

SQS [fair queues](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-fair-queues.html) stop one noisy tenant from delaying everyone else in a shared standard queue. They are enabled by sending a `MessageGroupId` (your tenant or customer id) with each message. Foundatio exposes this as `QueueEntryOptions.GroupId`:

```csharp
// Derive the tenant from the payload once...
var queue = new SQSQueue<ImportRequest>(o => o
    .ConnectionString(connectionString)
    .Name("imports")
    .GroupId(r => r.TenantId));

await queue.EnqueueAsync(importRequest);

// ...or set it per call
await queue.EnqueueAsync(importRequest, new QueueEntryOptions { GroupId = "tenant-123" });
```

Things to know:

- **A standard queue is enough.** No queue setting and no consumer change are needed, and messages in the same group can still be processed in parallel. `GroupId` on a standard queue does not provide ordering.
- **FIFO queues use the same field for ordering.** On a `.fifo` queue, messages in a group are delivered one at a time in order, and SQS requires a group id on every message.
- **Group ids are limited to 128 characters** of alphanumerics and punctuation (no spaces).
- **Set it on every message.** SQS treats a message without a group id as its own tenant.
- **Consumer concurrency matters.** SQS marks a tenant as noisy when it holds more than 10% of in-flight messages (with at least 30 in flight) or more than 10% of recent processing time. `SQSQueue` receives one message per worker loop, so run enough workers or instances for the in-flight share to be meaningful.
- **Monitor with CloudWatch.** Compare `ApproximateNumberOfMessagesVisible` with the `...InQuietGroups` metrics to confirm quiet tenants are not affected.

::: warning
Group id support for SQS requires a Foundatio.AWS version that includes it. Other queue types do not use `GroupId` for delivery. See [Message Groups](/guide/queues#message-groups).
:::

### Dead Letter Behavior

`SQSQueue` creates a `<name>-deadletter` queue with a native SQS redrive policy, and also moves a message there itself once `Retries` is exceeded. For FIFO queues the dead-letter queue is `<name>-deadletter.fifo`, because AWS requires FIFO queue names to end in `.fifo` and the dead-letter queue of a FIFO queue to be a FIFO queue ([CreateQueue](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_CreateQueue.html)). If `SupportDeadLetter` is `false` or the queue has no redrive policy, the message is deleted once `Retries` is exceeded. If the redrive policy names a dead-letter queue that can't be resolved (for example, it was deleted or access is denied), the error is logged and the message is kept on the source queue, so it becomes visible again and is retried (or moved by the native redrive policy) rather than lost. `StartWorkingAsync` workers log the error, back off and keep running (see [Worker Error Handling](../queues.md#worker-error-handling)). `GetDeadletterItemsAsync` is not supported for SQS; read the dead-letter queue directly.

## Next Steps

- [File Storage Guide](/guide/storage) - Usage patterns
- [Queues Guide](/guide/queues) - Queue processing patterns
- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [Serialization](/guide/serialization) - Configure serialization
