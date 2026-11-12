![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.Kafka.svg?style=flat)](https://www.nuget.org/packages/Foundatio.Kafka/)
[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FFoundatio.Kafka%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Foundatio.Kafka/latest/download)
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
| [**Messaging**](https://foundatio.dev/guide/messaging) | Pub/sub with Redis, RabbitMQ, Kafka, Azure Service Bus |
| [**Jobs**](https://foundatio.dev/guide/jobs) | Background job processing with queue integration |
| [**File Storage**](https://foundatio.dev/guide/storage) | Unified file API for disk, S3, Azure Blob, and more |
| [**Resilience**](https://foundatio.dev/guide/resilience) | Retry policies, circuit breakers, and timeouts |

## 🚀 Quick Start

```bash
dotnet add package Foundatio.Kafka
```

```csharp
// Messaging
IMessageBus messageBus = new KafkaMessageBus(o => o
    .BootstrapServers("localhost:9092"));
await messageBus.PublishAsync(new MyMessage { Data = "Hello" });
```

### Message transport (preview)

`KafkaMessageTransport` plugs Kafka into Foundatio's newer messaging (`IMessageClient`, `AddConsumer`, `AddSubscriber`) and supports queues (competing consumers), topics with durable and per-instance subscriptions, dead letters, delays and ordered groups.

```csharp
services.AddFoundatioMessaging(m => m
    .UseKafka(new KafkaMessageTransportOptions
    {
        BootstrapServers = "localhost:9092",
        Partitions = 6,
        ReplicationFactor = 3
    }));
```

- `UseKafka("localhost:9092")` uses the default options.
- `UseKafka(sp => new KafkaMessageTransportOptions { ... })` builds the options from the service provider, for example from configuration.
- The service provider creates the transport and disposes it at shutdown. `LoggerFactory` and `TimeProvider` come from DI unless the options set them.

How it maps to Kafka:

- **Queue**: a topic read by one consumer group. **Topic**: a Kafka topic with one consumer group per durable subscription; a new subscription starts at the end of the topic. **Instance subscription**: reads every partition directly from the end, without a consumer group, so a stopped process leaves nothing behind.
- **Settlement**: offsets are committed up to the lowest unfinished message of each partition, so messages are handled concurrently. Leases never expire; unsettled messages are delivered again when the receiver closes or a rebalance moves their partition (`RebalanceGracePeriod` lets in-flight messages finish first; after it, renewing reports the loss and Foundatio cancels the handler).
- **Retries and releases**: messages without a group key are copied, with an incremented delivery count, to a topic only their source reads (the queue itself, or the subscription's own `<prefix>.subscription.<topic>.<name>.<hash>` topic, which also receives `MessageDestination.Subscription` sends such as dead-letter replays). Messages with a group key are retried in place so their group stays in order; their delivery count and retry time are kept in the state topic (below) when their partition moves or the receiver closes, and within about two seconds of each retry.
- **Dead letters**: `<prefix>.deadletter.queue.<name>` and `<prefix>.deadletter.subscription.…`, receivable as `MessageSource.DeadLetter(source)`. Peeking leaves them in place and in order (an abandoned dead letter is released where it is). A dead-letter queue is read by one consumer group, so two peeks or replays of it at the same time split its partitions, and each sees only part of it.
- **Delays**: shared `<prefix>.delay.<tier>` topics (1s to 6h, `LogAppendTime`) and a forwarder that runs in every process with a receiver. Delays under a second are rounded up to about a second; Foundatio holds retries that short in-process instead (`MinRetryDelay`). A delayed message with a group key goes straight to its group's partition and the receiver holds it, and the group's later messages, in memory until it is due; this keeps the group in send order, but the partition's committed offset cannot pass it meanwhile. What is finished behind it is kept in the state topic, so the partition's next owner does not deliver it again (after a crash, what was finished since the last write, made every few seconds once 1,000 or more are behind it, is). A receiver holds at most 10,000 such messages (16 MB): past that it stops reading the partitions they are on until half of them are due, which holds up those partitions' other messages too. Use grouped delays for short delays or few messages, and delays without a group key, which wait in the delay topics, for long ones. The forwarder sends records only to the prefix's queue, topic, subscription and dead-letter topics; it drops a record whose target keeps rejecting it on its own (too large for the topic, not authorized) after 12 attempts, logging an error, and a forwarder that fails is restarted.
- **Groups**: the group key is the Kafka record key (murmur2, like the Java client). A receive returns a group's messages in order, and none while an earlier one of that group is unsettled. Adding partitions to a topic moves most groups to another partition: until the messages sent before are handled, a group's later messages can be handled before, or alongside, its earlier ones, by another receiver.
- **Flow control**: a receiver buffers up to 8 × `PrefetchCount` records (256 to 4,096, at most 8 MB) ready to hand out and stops reading while the buffer is full; librdkafka fetches ahead up to `queued.max.messages.kbytes` per receiver, which the transport sets to 16 MB (`ConfigureConsumer` can change it). Every queue and subscription receiver has its own consumer.
- **State**: `<prefix>.state`, a compacted topic, keeps what a partition's committed offset cannot say: the delivery counts and retry times of grouped retries, and the offsets finished behind an unfinished message. A receiver writes a partition's state when it releases the partition and soon after it changes, and reads it before it hands out the partition's messages; a state applies only to the records it was written for. With `CreateTopics = false`, provision it with `cleanup.policy=compact`; without it, receivers work as before, but grouped retries' counts restart and finished messages are delivered again when a partition moves.
- **Deletion**: `DeleteAsync` deletes the source's consumer group, its own topics and its dead-letter topic (never the shared topic of a subscription), and fails while a receiver is still open on it.
- Topics are created with the admin client (`CreateTopics = false` if they are provisioned for you). Names never contain `_`, which Kafka treats as equal to `.`; names with `_` or several parts get a short hash.
- **Retention**: queue, topic and subscription topics keep records for the broker's retention (`log.retention.hours`, 7 days by default) or what `TopicConfigs` sets, so a backlog older than that is deleted before it is handled. They must not be compacted: records without a group key have no key, and compaction would keep only the last message of each group. Dead-letter topics keep records for at least 14 days and delay tiers for at least 7 days, whatever their size and whatever `TopicConfigs` says, and neither is ever compacted. The transport does not change topics that already exist. With `CreateTopics = false`, provision delay tiers with `message.timestamp.type=LogAppendTime`, `cleanup.policy=delete` and a retention well above 6 hours, and dead-letter topics with the retention you want for them.

Limits: a message is counted when it is retried or released, so one that crashes the process while it is handled is retried until it is skipped by hand, and a grouped retry counted less than about two seconds before a crash is counted again; a crash after a retry's copy is written but before the original's offset is committed (within `CommitInterval`) delivers both; partitions added to a topic are not picked up by running instance subscriptions, and reorder groups (see Groups); there is no deduplication; and a durable subscription whose committed offsets the broker expired (`offsets.retention.minutes`, 7 days by default) starts again at the end of the topic.

## 📦 Provider Implementations

| Provider | Caching | Queues | Messaging | Storage | Locks |
|----------|---------|--------|-----------|---------|-------|
| [In-Memory](https://foundatio.dev/guide/implementations/in-memory) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Redis](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Redis) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Azure Storage](https://github.com/FoundatioFx/Foundatio.AzureStorage) | | ✅ | | ✅ | |
| [Azure Service Bus](https://github.com/FoundatioFx/Foundatio.AzureServiceBus) | | ✅ | ✅ | | |
| [AWS (S3/SQS/SNS)](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AWS) | | ✅ | ✅ | ✅ | |
| [RabbitMQ](https://github.com/FoundatioFx/Foundatio.RabbitMQ) | | | ✅ | | |
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
dotnet add package Foundatio.Kafka --prerelease
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

[![contributors](https://contributors-img.web.app/image?repo=foundatiofx/Foundatio.Kafka)](https://github.com/foundatiofx/Foundatio.Kafka/graphs/contributors)
