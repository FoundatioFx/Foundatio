![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.AWS.svg?style=flat)](https://www.nuget.org/packages/Foundatio.AWS/)
[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FFoundatio.AWS%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Foundatio.AWS/latest/download)
[![Discord](https://img.shields.io/discord/715744504891703319)](https://discord.gg/6HxgFCx)

Pluggable foundation blocks for building loosely coupled distributed apps.

## ✨ Why Choose Foundatio?

- 🔌 **Pluggable implementations** - Swap Redis, Azure, AWS, or in-memory with no code changes
- 🧪 **Developer friendly** - In-memory implementations for fast local development and testing
- 💉 **DI native** - Built for Microsoft.Extensions.DependencyInjection
- 🎯 **Interface-first** - Code against abstractions, not implementations
- ⚡ **Production ready** - Battle-tested in high-scale applications
- 🔄 **Consistent APIs** - Same patterns across caching, queues, storage, and more

## 🧱 Core Building Blocks

| Feature | Description |
|---------|-------------|
| [**Caching**](https://foundatio.dev/guide/caching) | In-memory, Redis, and hybrid caching with automatic invalidation |
| [**Queues**](https://foundatio.dev/guide/queues) | Reliable message queuing with Redis, Azure, AWS SQS |
| [**Locks**](https://foundatio.dev/guide/locks) | Distributed locking and throttling |
| [**Message Bus (legacy)**](https://foundatio.dev/guide/message-bus) | `IMessageBus` pub/sub with Redis, AWS SNS/SQS, RabbitMQ, Kafka, Azure Service Bus |
| [**Jobs**](https://foundatio.dev/guide/jobs) | Background job processing with queue integration |
| [**Messaging**](https://foundatio.dev/guide/messaging) | Send commands and publish events to DI handlers, with retries and dead letters |
| [**Durable Jobs**](https://foundatio.dev/guide/durable-jobs) | Tracked background jobs with arguments, progress, cancellation and CRON |
| [**File Storage**](https://foundatio.dev/guide/storage) | Unified file API for disk, S3, Azure Blob, and more |
| [**Resilience**](https://foundatio.dev/guide/resilience) | Retry policies, circuit breakers, and timeouts |

## 🚀 Quick Start

```bash
dotnet add package Foundatio.AWS
```

```csharp
// Queuing (SQS)
IQueue<WorkItem> queue = new SQSQueue<WorkItem>(o => o
    .ConnectionString("serviceurl=http://localhost:4566"));
await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();

// Messaging (SQS)
IMessageBus messageBus = new SQSMessageBus(o => o
    .ConnectionString("serviceurl=http://localhost:4566"));
await messageBus.PublishAsync(new MyMessage { Data = "Hello" });

// File Storage (S3)
IFileStorage storage = new S3FileStorage(o => o
    .ConnectionString("serviceurl=http://localhost:4566")
    .Bucket("my-bucket"));
await storage.SaveFileAsync("docs/readme.txt", "Hello World");
```

The examples above point at [Floci](https://github.com/floci-io/floci), a free local AWS emulator. Run `docker compose up -d` from the repository root to start it on `http://localhost:4566`.

### Message transport (preview)

`SqsMessageTransport` plugs SQS and SNS into Foundatio's newer messaging (`IMessageClient`, `AddConsumer`, `AddSubscriber`) and supports queues, topics with durable and per-instance subscriptions, dead letters, delays and, in FIFO mode, ordered groups and deduplication.

```csharp
services.AddFoundatioMessaging(m => m
    .UseSqs(new SqsMessageTransportOptions
    {
        ConnectionString = "Region=us-east-1",
        Prefix = "prod-",
        Fifo = false
    }));
```

- `UseSqs("Region=us-east-1")` connects with a connection string and the default options.
- `UseSqs()`, with no connection string, uses the `IAmazonSQS` and `IAmazonSimpleNotificationService` registered in DI (for example with `AddAWSService<IAmazonSQS>()` and `AddAWSService<IAmazonSimpleNotificationService>()`), which stay yours; with neither registered, it uses the default AWS credential and region resolution, such as an IAM role. `UseSqs(sqs, sns, options)` takes the clients instead.
- `UseSqs(sp => new SqsMessageTransportOptions { ... })` builds the options from the service provider, for example from configuration.
- The service provider creates the transport and disposes it, with the SQS and SNS clients it created, at shutdown. `LoggerFactory` and `TimeProvider` come from DI unless the options set them; the clock is `MessagingOptions.TimeProvider` when that is set.

How it maps to AWS:

- **Queue**: an SQS queue. **Topic**: an SNS topic. **Durable subscription**: an SQS queue subscribed to the topic with raw delivery. **Instance subscription**: a queue per receiver, deleted with it, or by a later receiver once its heartbeat tag is older than `InstanceSubscriptionTimeout`. Every source has its own `-dlq` queue. Queues keep messages for 14 days.
- **Leases** are visibility timeouts, which SQS ends 12 hours after the receive however often they are renewed.
- **Groups**: with `Fifo = true`, queues and topics are FIFO, group keys are ordered and deduplication ids are dropped within 5 minutes. Without it, a group key sent to a queue or subscription is also its SQS message group id, so [SQS fair queues](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-fair-queues.html) keep one busy tenant from delaying the others; SNS standard topics do not take a group id.
- **Delays**: sends and publishes can be delayed by up to 13 days. Standard queues delay a send natively for up to 15 minutes. The rest of a longer send's delay, every publish delay and every FIFO delay are applied by hiding the message with a visibility timeout when it arrives early, and again every 11 hours, until it is due: one receive and one visibility change per 11 hours. Only a send delayed by more than 13 days is sent again, after 13 days.
- **Receives**: a receive long-polls for up to 20 seconds and returns at most 10 messages, so a receiver takes up to 16 receives at once (`MaxConcurrentReceives`) to keep handlers busy, and Foundatio receives ahead of free handler slots (`ReceiveAhead`): messages wait, leased and renewed, until a handler is free. While the queue is drained, only one receive long-polls; each that returns a full batch lets one more poll. The 16 receives and reading ahead were measured on the Floci emulator, not yet on real AWS, which may return short batches from a backlog and so park receives sooner. SQS hands each message, and each FIFO group with a message in flight, to one receive at a time. The early messages a receive gets are hidden with one `ChangeMessageVisibilityBatch` call, or sent again with one `SendMessageBatch` and one `DeleteMessageBatch`. A cancelled receive (a host stopping) leaves its long poll open until it ends, since SQS can still hand messages to a request the client gave up on, and makes what it gets visible again at once instead of leaving it hidden for the lease; those messages count one more receive.
- **Settlement**: each receiver sends completions as `DeleteMessageBatch` calls, and renewals and abandons as `ChangeMessageVisibilityBatch` calls, of up to 10 messages, as a group commit. A settlement goes out at once while no call of its kind is in flight, so a lone message waits for nothing; otherwise it joins the next call, sent when one returns or once 10 are waiting. Under load that is about one delete call per 10 messages instead of one per message, and a FIFO queue's 300 calls per second per action complete up to 3,000 messages a second.
- **Deletion**: `DeleteAsync` keeps the queues (SQS blocks a deleted queue's name for 60 seconds). It deletes the messages the queue held when the call started, keeps what is sent meanwhile, and tags the queue so receivers opened afterwards discard older messages it could not see. Messages it receives while running are hidden until it ends, which adds one to their delivery count. A receiver still open on a deleted durable subscription checks, every 5 seconds while it receives nothing (one `GetQueueAttributes` call), that its queue may still get the topic's messages, and faults when it may not, so Foundatio opens a new receiver, which subscribes the queue again.

Things to plan for:

- **In-flight quota**: hidden messages count toward SQS's quota of about 120,000 messages in flight per queue, past which SQS refuses every receive of the queue with `OverLimit`, real work included. Each pending delayed publish and FIFO send counts, and so does each standard-queue send delayed by more than 15 minutes once those 15 minutes have passed. On a standard queue with 50,000 in flight, an early message is instead sent again with a delay of up to 15 minutes, which does not count but costs a send, a receive and a delete every 15 minutes until it is due or the queue has fewer in flight.
- **Bursts of long delays**: when many long-delayed messages become visible at once (say 300,000 reminders sent together, 15 minutes later), every receiver of the queue hides them at once. Each reads the queue's in-flight count again after every 1,000 it hides, so together they stop near 50,000 plus 1,000 per receiver, which leaves room for about 70 receivers. SQS's count is approximate and can lag, so more receivers, or a count that lags, can still overrun the quota. A receiver that SQS then refuses with `OverLimit` keeps running: for a minute it sends early messages again instead of hiding them, and it releases the last 1,000 it hid so they are sent again too, which frees their place. FIFO queues can only hide, so a burst of long-delayed FIFO messages can stall a queue until the hides end. Use jobs for long or high-volume delays.
- **FIFO groups behind a delay**: a hidden FIFO message holds back the rest of its group, and each hide adds one to the delivery count (the attempt number) of every message waiting behind it. Messages queued behind one delayed by days start with fewer retries, or none.
- **New subscriptions**: queues are created with the policy that lets their topic send to them. A subscription whose queue already existed without that permission, for example one re-created after `DeleteAsync`, gets it with `SetQueueAttributes`, which AWS can take a minute to apply; SNS drops what it is not allowed to deliver meanwhile.
- **Names**: an empty `Prefix` shares names with `SQSQueue<T>` and `SQSMessageBus`, whose messages the transport cannot read. Use a prefix while both run side by side.

Monitoring: `GetStatsAsync` reads `GetQueueAttributes` for the source's queue and its `-dlq` queue, two calls with cached queue URLs. SQS's counts are approximate, and can take a minute to catch up after sends and settlements.

| Count | On SQS |
|---|---|
| `Queued` | `ApproximateNumberOfMessages`, the visible messages. That includes delayed messages SQS could not delay itself, until a receiver first hides them. |
| `Delayed` | `ApproximateNumberOfMessagesDelayed`, the delays SQS applies: sends to a standard queue or subscription, for up to 15 minutes (the first 15 minutes of a longer delay). Null with `Fifo = true`, where SQS delays nothing. |
| `InFlight` | `ApproximateNumberOfMessagesNotVisible`, every hidden message: deliveries, but also retries waiting out their delay and early messages hidden until due. |
| `DeadLettered` | Every message in the `-dlq` queue, including those a receiver is reading. |

- Instance subscriptions are not counted (`GetStatsAsync` returns null), since each has its own queue.
- A queue that does not exist counts as empty, and nothing is created.
- After `DeleteAsync`, messages that were hidden or delayed during the delete still count until a receiver discards them.

IAM actions the transport needs at runtime:

- `sqs:CreateQueue`, `sqs:GetQueueUrl`, `sqs:GetQueueAttributes`, `sqs:SetQueueAttributes`, `sqs:TagQueue`, `sqs:ListQueueTags`, `sqs:ListQueues`, `sqs:DeleteQueue`, `sqs:SendMessage`, `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:ChangeMessageVisibility`
- `sns:CreateTopic`, `sns:Publish`, `sns:Subscribe`, `sns:Unsubscribe`, `sns:ListSubscriptionsByTopic`, `sns:SetSubscriptionAttributes`

## 📦 Provider Implementations

| Provider | Caching | Queues | Messaging | Storage | Locks |
|----------|---------|--------|-----------|---------|-------|
| [In-Memory](https://foundatio.dev/guide/implementations/in-memory) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Redis](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Redis) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Azure Storage](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AzureStorage) | | ✅ | | ✅ | |
| [Azure Service Bus](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AzureServiceBus) | | ✅ | ✅ | | |
| [AWS (S3/SQS/SNS)](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AWS) | | ✅ | ✅ | ✅ | |
| [RabbitMQ](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.RabbitMQ) | | | ✅ | | |
| [Kafka](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Kafka) | | | ✅ | | |
| [Aliyun](https://github.com/FoundatioFx/Foundatio.Aliyun) | | | | ✅ | |
| [SFTP](https://github.com/FoundatioFx/Foundatio.Storage.SshNet) | | | | ✅ | |

## 📚 Learn More

**👉 [Complete Documentation](https://foundatio.dev)**

### Core Features

- [Getting Started](https://foundatio.dev/guide/getting-started) - Installation and setup
- [Caching](https://foundatio.dev/guide/caching) - In-memory, Redis, and hybrid caching with invalidation
- [Queues](https://foundatio.dev/guide/queues) - FIFO message delivery with lock renewal and retry policies
- [Locks](https://foundatio.dev/guide/locks) - Distributed locking with null handling patterns
- [Messaging](https://foundatio.dev/guide/messaging) - Pub/sub with size limits and notification patterns
- [File Storage](https://foundatio.dev/guide/storage) - Unified file API across providers
- [Jobs](https://foundatio.dev/guide/jobs) - Background job processing and hosted service integration

### Advanced Topics

- [Resilience](https://foundatio.dev/guide/resilience) - Retry policies, circuit breakers, and timeouts
- [Serialization](https://foundatio.dev/guide/serialization) - Serializer configuration and performance
- [Dependency Injection](https://foundatio.dev/guide/dependency-injection) - DI setup and patterns
- [Configuration](https://foundatio.dev/guide/configuration) - Options and settings

## 📦 CI Packages (Feedz)

Want the latest CI build before it hits NuGet? Add the Feedz source and install the pre-release version:

```bash
dotnet nuget add source https://f.feedz.io/foundatio/foundatio/nuget -n foundatio-feedz
dotnet add package Foundatio.AWS --prerelease
```

Or add to your `NuGet.config`:

```xml
<configuration>
  <packageSources>
    <add key="foundatio-feedz" value="https://f.feedz.io/foundatio/foundatio/nuget" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="foundatio-feedz">
      <package pattern="Foundatio.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request. See our [documentation](https://foundatio.dev) for development guidelines.

**Development Setup:**

1. Clone the [Foundatio repository](https://github.com/FoundatioFx/Foundatio)
2. Open `Foundatio.slnx` in Visual Studio or VS Code
3. Run `dotnet build` to build
4. Run `dotnet test` to run tests

## 📄 License

Apache 2.0 License

## Thanks to all the people who have contributed

[![contributors](https://contributors-img.web.app/image?repo=foundatiofx/Foundatio.AWS)](https://github.com/foundatiofx/Foundatio.AWS/graphs/contributors)
