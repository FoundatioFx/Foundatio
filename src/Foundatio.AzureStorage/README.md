![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.AzureStorage.svg?style=flat)](https://www.nuget.org/packages/Foundatio.AzureStorage/)
[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FFoundatio.AzureStorage%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Foundatio.AzureStorage/latest/download)
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
dotnet add package Foundatio.AzureStorage
```

```csharp
// Queuing
IQueue<WorkItem> queue = new AzureStorageQueue<WorkItem>(o => o
    .ConnectionString("UseDevelopmentStorage=true"));
await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();

// File Storage
IFileStorage storage = new AzureFileStorage(o => o
    .ConnectionString("UseDevelopmentStorage=true")
    .ContainerName("my-container"));
await storage.SaveFileAsync("docs/readme.txt", "Hello World");
```

### Message transport (preview)

`AzureStorageQueueMessageTransport` plugs Azure Storage Queues into Foundatio's newer messaging (`IMessageClient`, `AddConsumer`, `AddSubscriber`) and supports queues, topics with durable and per-instance subscriptions, dead letters and delays of up to 7 days. It doesn't order groups or deduplicate.

```csharp
services.AddFoundatioMessaging(m => m
    .UseAzureStorageQueues(builder.Configuration.GetConnectionString("AzureStorage")!)
    .UseServiceName("billing")
    .AddConsumer<SendReceiptHandler>());

// Or with options, for example Microsoft Entra ID
m.UseAzureStorageQueues(new AzureStorageQueueMessageTransportOptions
{
    ServiceUri = new Uri("https://myaccount.queue.core.windows.net"),
    Credential = new DefaultAzureCredential(),
    Prefix = "prod-"
});
```

- `UseAzureStorageQueues("UseDevelopmentStorage=true")` connects to Azurite. Set `ConnectionString`, or `ServiceUri` with `Credential`, not both. A `ServiceUri` with a SAS token works without a credential.
- **Permissions**: the identity needs the Storage Queue Data Contributor role. Receivers create their queues and set their metadata (`Create Queue`, `Set Queue Metadata`), instance receivers delete theirs, durable subscription receivers read their metadata, and publishes list queues (`List Queues`); the Message Processor and Message Sender roles allow none of these. There is no option to skip creating queues.
- `UseAzureStorageQueues()`, with no connection in the options, uses the `QueueServiceClient` registered in DI (for example by `AddAzureClients`), which stays yours. Its clients must keep the default `MessageEncoding` (`None`): the transport encodes messages itself.
- `UseAzureStorageQueues(sp => new AzureStorageQueueMessageTransportOptions { ... })` builds the options from the service provider, for example from configuration.
- `LoggerFactory` and `TimeProvider` come from DI unless the options set them; the clock is `MessagingOptions.TimeProvider` when that is set. The clock is only what delivery times are measured against: polls, heartbeats and leases run on real time, so a fake clock that doesn't advance never stalls a receive.

How it maps to Storage Queues:

- **Queue**: a Storage queue. Storage has no topics, so a **durable subscription** is a queue of its own, named after its topic, and a **publish** lists the topic's subscription queues (one `List Queues` call, not cached, so a subscription another process just opened gets every later publish, which makes a publish two round trips in a row where a send is one) and sends a copy to each. A publish is not atomic: an instance queue removed between the listing and the send (its receiver was disposed) is skipped, a durable subscription's queue is created again, and when a send fails, the others still finish and the publish then throws one `MessageBusException` naming the queues that failed; the rest got the message, so retrying the publish sends them a second copy. An **instance subscription** is a queue per receiver, deleted with it, or by a later publish or instance receiver of the topic once its heartbeat (queue metadata, updated every third of `InstanceSubscriptionTimeout`, 5 minutes by default) has lapsed. Heartbeats are written and judged on Storage's clock, the `Date` of its responses, so machines whose clocks disagree don't remove each other's live instance queues. Every queue and durable subscription has a dead-letter queue: its name plus `-poison`.
- **Names**: Storage queue names are 3 to 63 lowercase letters, digits and hyphens, so a queue is named by a readable, shortened form of the source's names and a hash of the exact names, for example `prod-orders-1a2b3c4d`, and its dead letters go to `prod-orders-1a2b3c4d-poison`; a subscription's queue starts with its topic, as in `prod-shop-orders-5e6f7a8b-billing-9c0d1e2f`. Names that differ only in case or punctuation get their own queues. `Prefix` is at most 16 lowercase letters, digits and single hyphens, and is followed by a hyphen whether or not it ends with one. `transport.GetQueueName(source)` returns a queue's or dead-letter queue's name, for infrastructure as code and alerts.
- **Leases** are visibility timeouts, of up to 7 days. A renewal or abandon updates the message, which gives it a new pop receipt; each delivery's settlements run one at a time so the next one uses it, and a cancelled one still runs to completion, so its receipt is never lost. A settlement Storage rejects for its pop receipt (the message was received again, or cleared) throws `MessageLeaseLostException`.
- **Delays**: sends, publishes and retries are delayed natively with a visibility timeout, in whole seconds, for up to 7 days; Foundatio sends longer delays again. `DeliveryCount` is Storage's dequeue count.
- **Retention**: messages never expire by default. Storage's own default is 7 days, which would silently drop a backlog nobody received; set `MessageTimeToLive` to bound it (delays are then limited to less than it). Dead letters never expire, whatever `MessageTimeToLive` is: they stay until they are replayed or deleted.
- **Receives**: Storage cannot long-poll. A receive polls, and each empty poll doubles the wait before the next, from `MinPollInterval` (50 ms) to `MaxPollInterval` (2 s); a message received resets it. The wait carries over between receives, so an idle receiver polls every 2 seconds: about 43,000 billed transactions a day per receiver, and a message from another process waits up to about 2 seconds. Sends and retries through the same transport wake its own receivers of that queue at once. A receive returns up to 32 messages, Foundatio runs up to 8 at once on one receiver while there is a backlog, and while the queue is drained only one polls. A cancelled receive lets its request finish and makes what it got visible again at once.
- **Settlement**: Storage has no batch delete or update, so each complete is a `Delete Message` call and each renewal or abandon an `Update Message` call. A batch send makes up to 32 `Put Message` calls at once.
- **Size**: Storage takes 64 KiB of message text. The transport writes a binary envelope (id, group key, headers, body) as base64, so binary bodies fit, which leaves 46 KiB for a message as `MessageSize` measures it (body, headers and ids), keeping 2 KiB for framing and the dead-letter reason. A larger message, or one whose headers leave no room for a 1,024-byte dead-letter reason, throws `MessageTooLargeException`: store large payloads with `IFileStorage` (for example `AzureFileStorage`) and send a reference.
- **Other producers**: a message that isn't the transport's envelope, such as one written by Event Grid, Azure Functions or the Storage SDK, is delivered as its body (base64-decoded when it is base64, so plain text that happens to be valid base64, such as `ping`, is decoded too), with Storage's message id and no headers. Its dead letter keeps its text whole, up to Storage's 64 KiB, behind a short prefix with its id and reason.
- **Deletion**: `DeleteAsync` clears the queue and its `-poison` queue, delayed and leased messages included, and keeps the queues, since Storage does not let a deleted queue's name be used again for at least 30 seconds. A deleted durable subscription is marked as unsubscribed in its metadata, so publishes skip it until a receiver opens or a message is sent to it. A receiver still open on it checks every 3 seconds while it receives nothing (one `Get Queue Metadata` call) and faults once it was deleted, so Foundatio opens a new receiver, which subscribes again.

Monitoring: `GetStatsAsync` reads the approximate message count Storage keeps for the source's queue and its `-poison` queue, one `Get Queue Metadata` call each.

| Count | On Storage Queues |
|---|---|
| `Queued` | `ApproximateMessagesCount`: every message in the queue, including those being handled and delayed ones, since Storage reports one count |
| `Delayed` | null |
| `InFlight` | null |
| `DeadLettered` | `ApproximateMessagesCount` of the `-poison` queue |

- Instance subscriptions are not counted (`GetStatsAsync` returns null), since each has its own queue.
- A queue that does not exist counts as empty, and nothing is created.

Things to plan for:

- **Throughput**: Storage's scalability target is about 2,000 messages a second per queue, and every message costs at least a send, a delete and part of a receive. Spread heavy traffic over several queues, or use Service Bus.
- **Azurite**: the tests run on Azurite, which serves requests on one thread (a few hundred messages a second) and does not block a deleted queue's name. Real Azure does both.
- **Crashes**: a crash between writing a dead letter and deleting the original leaves the message to be dead-lettered again, so the `-poison` queue may hold it twice, as on RabbitMQ, SQS and Kafka. A batch send that fails partway throws after some of its messages were sent.
- **The legacy queue**: `AzureStorageQueue<T>` and the transport use different queues and message formats. Drain a legacy queue with `AzureStorageQueue<T>` before you move its consumers.
- **Events**: every subscription polls, so an event from another process can wait up to `MaxPollInterval`, and each instance subscription is another polling queue. An app that also runs Redis or Service Bus can keep its queues here and put its topics, and the legacy bus of `AddLegacyMessageBus()`, there: `m.UseAzureStorageQueues(...).ForTopics(t => t.UseRedis())`. See [topics on another transport](https://foundatio.dev/guide/messaging#topics-on-another-transport).

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
dotnet add package Foundatio.AzureStorage --prerelease
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

[![contributors](https://contributors-img.web.app/image?repo=foundatiofx/Foundatio.AzureStorage)](https://github.com/foundatiofx/Foundatio.AzureStorage/graphs/contributors)
