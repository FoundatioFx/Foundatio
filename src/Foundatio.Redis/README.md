![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.Redis.svg?style=flat)](https://www.nuget.org/packages/Foundatio.Redis/)
[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FFoundatio.Redis%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Foundatio.Redis/latest/download)
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
dotnet add package Foundatio.Redis
```

```csharp
// Caching
var muxer = await ConnectionMultiplexer.ConnectAsync("localhost");
ICacheClient cache = new RedisCacheClient(o => o.ConnectionMultiplexer(muxer));
await cache.SetAsync("user:123", user, TimeSpan.FromMinutes(5));
var cached = await cache.GetAsync<User>("user:123");

// Queuing
IQueue<WorkItem> queue = new RedisQueue<WorkItem>(o => o.ConnectionMultiplexer(muxer));
await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();

// Messaging
IMessageBus messageBus = new RedisMessageBus(o => o.Subscriber(muxer.GetSubscriber()));
await messageBus.PublishAsync(new MyMessage { Data = "Hello" });

// File Storage
IFileStorage storage = new RedisFileStorage(o => o.ConnectionMultiplexer(muxer));
await storage.SaveFileAsync("docs/readme.txt", "Hello World");
```

## Redis message transport

`RedisMessageTransport` runs Foundatio messaging (`IMessageClient`, consumers, durable and instance subscriptions) on Redis Streams. Register it with `UseRedis`, on the multiplexer your app already uses:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(await ConnectionMultiplexer.ConnectAsync("localhost"));
builder.Services.AddFoundatioMessaging(m => m
    .UseRedis(new RedisMessageTransportOptions
    {
        Prefix = "myapp:messaging",
        DeadLetterRetention = TimeSpan.FromDays(14)
    })
    .AddConsumer<SendReceiptHandler>());
```

- `UseRedis()`, with no options, uses the defaults. `UseRedis(sp => new RedisMessageTransportOptions { ... })` builds the options from the service provider, for example from configuration.
- Both use the `IConnectionMultiplexer` registered in DI, and the host fails to start without one. `UseRedis(connection, options)` takes the connection instead.
- The service provider creates the transport and disposes it at shutdown. The connection stays yours.

It supports ordered groups, deduplication, delays of any length and lease expiry, on a single Redis, Sentinel or Redis Cluster. Each queue and durable subscription lives on one cluster slot; a topic's instance subscriptions all live on the topic's slot. It needs Redis 6.2 or later, or Valkey.

### Options

| Option | Default | |
|---|---|---|
| `Prefix` | `foundatio:messaging` | Prepended to every key and wake-up channel. Cannot contain `{` or `}`. |
| `Database` | `-1` | The database to use; `-1` is the connection's default. |
| `MaxMessageSize` | 16 MiB | The largest message, measured by `MessageSize.Of`. Can be raised to 256 MiB. |
| `DeduplicationWindow` | 5 minutes | How long a `DeduplicationId` is remembered. |
| `MinPollInterval` / `MaxPollInterval` | 20 ms / 500 ms | The backoff between polls of an empty source. |
| `InstanceSubscriptionTimeout` | 30 seconds | How long an instance subscription survives without a heartbeat. |
| `DeadLetterMaxLength` | none | The most dead letters kept per dead-letter queue; the oldest are removed first. |
| `DeadLetterRetention` | none | How long a dead letter is kept. |
| `TimeProvider`, `LoggerFactory` | from DI | Without `UseRedis`, the system clock and no logging. |

### Redis must not evict its keys

Each source is a stream with a few sorted sets and hashes beside it. If Redis evicts one, its messages are lost, and an ordered group can be left waiting on a message that no longer exists.

- Use `maxmemory-policy noeviction`, or a Redis that holds only messaging data. Under the `volatile-*` policies, instance subscriptions and deduplication ids go first, because they have expiry times. Under `allkeys-*`, any key can go, including a whole queue.
- The transport logs a warning when it starts if a primary has a `maxmemory` limit and another policy.
- When it finds that a stream or a delayed message was lost, it releases the groups that waited on it and logs a warning. The lost messages themselves can't be recovered.
- Redis replicates asynchronously, so a failover can lose a send or settle that was already acknowledged.
- When Redis loses keys without evicting them (a restart without persistence, a failover before replication, a flush), queues and jobs carry on with what is left. A durable subscription's receiver that finds its stream missing creates it again and registers it with its topic, and a receiver re-registers its subscription every 5 seconds while it receives, since the topic's list of subscriptions lives on another slot and can be lost alone; both log a warning. Messages published to the topic before that, typically for up to a few seconds, reach no subscription and are lost, and publishers log a warning once when a topic they saw subscriptions of has none.

### Memory and retention

Everything is kept in Redis memory, which is often shared with the app's cache and locks.

- **Fan-out:** publishing to a topic copies the full message into every durable and instance subscription of that topic.
- **Settled messages** are removed at once.
- **Dead letters** are kept until they are replayed or purged, unless you set `DeadLetterMaxLength` or `DeadLetterRetention`. Expired dead letters are removed when another message is dead-lettered into the same queue, or when the queue is read.
- **Durable subscriptions** stay on their topic, and keep getting a copy of every publish, until you delete them with `IMessageAdmin.DeleteAsync`. Delete the subscription of a service you rename or retire. The transport logs a warning when a subscription has 1,000 or more messages and none sent to it in the last hour has been received.
- **Instance subscriptions** are removed when their receiver is disposed. Publishers remove those that miss their heartbeats for `InstanceSubscriptionTimeout`, for example after a crash. A process that stalls longer than that misses what was published meanwhile, and logs a warning when its subscription is restored.

### Latency and cost

- Receivers poll their sources with Lua scripts on the shared multiplexer; there are no blocking reads.
- A receive that drains its source, while its receiver has room for more messages, registers the receiver as idle. Each message then made available, by a send, a publish, an abandon without a delay, the next message of a group or a disposed receiver's messages, wakes one idle receiver in any process, the one most recently busy, over that receiver's own pub/sub channel (`<Prefix>:{q:<queue>}:wake:<consumer>`, and so on). Delivery normally takes about a millisecond, and the other idle receivers don't poll. A delayed send wakes one idle receiver, so it learns when the message is due; a retry wakes none, as the receiver that abandoned it learns that.
- Wake-ups use sharded pub/sub (`SPUBLISH`) on Redis Cluster 7 or later, so only the source's shard sends them, and classic `PUBLISH` on a single Redis, Sentinel or an older cluster. They are published by the scripts themselves, so they cost no round trip.
- Polling backs off from `MinPollInterval` to `MaxPollInterval`, and is the fallback when a wake-up is lost, for example while a connection reconnects, or goes to a receiver that crashed. Each idle receiver runs about one script per `MaxPollInterval`.
- Publishing to a topic runs one script on the topic's slot that copies the message into every instance subscription, plus one script per durable subscription. With many instance subscriptions on a busy topic, that slot's shard does all of their work: their copies, receives and settles.
- Completes that a receiver's handlers call while one of its completes is running are sent together, as one script, when it ends.
- One receive returns at most about 8 MiB, but always at least one message. Large messages still block Redis while scripts copy them, so store large payloads elsewhere and send a reference.

### Monitoring

`GetStatsAsync` counts a queue's or durable subscription's messages with one script on its cluster slot, a handful of O(1) and O(log n) commands, so it is cheap to call every few seconds for every source. The counts are read at one moment and do not lag:

| Count | On Redis |
|---|---|
| `Queued` | Stream entries the consumer group has not read, plus messages held behind an unsettled message of their group, abandoned messages, messages whose lease lapsed and delayed messages that are due, until a receiver takes them. |
| `Delayed` | Messages sent with a later delivery time, and retries waiting out their delay. |
| `InFlight` | Messages leased to a receiver whose lease has not lapsed. |
| `DeadLettered` | The length of the source's dead-letter stream, once dead letters past `DeadLetterRetention` are removed. |

- No count is null. Instance subscriptions are not counted (`GetStatsAsync` returns null), since each belongs to one receiver.
- A source that does not exist counts as empty, and nothing is created.
- Unread entries are the group's `lag` where Redis reports it. Redis 6.2 does not, and Redis 7+ cannot after some deletes, such as completing the newest message read while an older one is pending. The transport then counts the stream's entries less the group's pending ones, which is the same number, because a settle acknowledges and deletes its entry together.

## Redis job store

`RedisJobStore` stores Foundatio durable jobs (`IJobClient`, `AddJob`, `AddCronJob`) in Redis, so every instance shares them and they survive restarts. Register it with `UseRedis`:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(await ConnectionMultiplexer.ConnectAsync("localhost"));
builder.Services.AddFoundatioJobs(j => j
    .UseRedis(new RedisJobStoreOptions { Prefix = "myapp:jobs" })
    .AddJob<GenerateReportJob>());
```

It has the same overloads as the transport: `UseRedis()`, `UseRedis(sp => options)` and `UseRedis(connection, options)`.

| Option | Default | |
|---|---|---|
| `Prefix` | `foundatio:jobs` | Prepended to every key and used as the store's hash tag, so on a cluster the whole store lives on one slot. Cannot contain `{` or `}`. |
| `Database` | `-1` | The database to use; `-1` is the connection's default. |

Every process that enqueues or runs the same jobs must use the same `Prefix` and `Database`.

## 📦 Provider Implementations

| Provider | Caching | Queues | Messaging | Storage | Locks |
|----------|---------|--------|-----------|---------|-------|
| [In-Memory](https://foundatio.dev/guide/implementations/in-memory) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Redis](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Redis) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Azure Storage](https://github.com/FoundatioFx/Foundatio.AzureStorage) | | ✅ | | ✅ | |
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
dotnet add package Foundatio.Redis --prerelease
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

[![contributors](https://contributors-img.web.app/image?repo=foundatiofx/Foundatio.Redis)](https://github.com/foundatiofx/Foundatio.Redis/graphs/contributors)
