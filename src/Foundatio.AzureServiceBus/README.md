![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.AzureServiceBus.svg?style=flat)](https://www.nuget.org/packages/Foundatio.AzureServiceBus/)
[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FFoundatio.AzureServiceBus%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Foundatio.AzureServiceBus/latest/download)
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
dotnet add package Foundatio.AzureServiceBus
```

```csharp
// Queuing
IQueue<WorkItem> queue = new AzureServiceBusQueue<WorkItem>(o => o
    .ConnectionString("Endpoint=sb://..."));
await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();

// Messaging
IMessageBus messageBus = new AzureServiceBusMessageBus(o => o
    .ConnectionString("Endpoint=sb://..."));
await messageBus.PublishAsync(new MyMessage { Data = "Hello" });
```

### Message transport (preview)

`AzureServiceBusMessageTransport` plugs Azure Service Bus into Foundatio's newer messaging (`IMessageClient`, `AddConsumer`, `AddSubscriber`) and supports queues, topics with durable and per-instance subscriptions, dead letters, delays and, in its session and duplicate detection modes, ordered groups and deduplication.

```csharp
services.AddFoundatioMessaging(m => m
    .UseAzureServiceBus(new AzureServiceBusMessageTransportOptions
    {
        ConnectionString = "Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...",
        Prefix = "prod-",
        RequiresSession = false
    }));
```

- `UseAzureServiceBus("Endpoint=sb://...")` uses the default options. Set `FullyQualifiedNamespace` and `Credential` (for example a `DefaultAzureCredential`) to connect with Microsoft Entra ID instead.
- `UseAzureServiceBus(sp => new AzureServiceBusMessageTransportOptions { ... })` builds the options from the service provider, for example from configuration.
- `UseAzureServiceBus(client, administrationClient, options)`, or `UseAzureServiceBus()` with a `ServiceBusClient` and `ServiceBusAdministrationClient` registered in DI, uses those clients, which stay yours.
- The service provider creates the transport and disposes it, with the clients it created, at shutdown. `LoggerFactory` and `TimeProvider` come from DI unless the options set them.

How it maps to Service Bus:

- **Queue**: a queue named `{Prefix}queue.{name}`. **Topic**: a topic named `{Prefix}topic.{name}` (queues and topics share one namespace in Service Bus, and a Foundatio queue and topic may share a name). **Durable subscription**: a topic subscription named after the service. **Instance subscription**: a subscription per receiver, deleted with it, which Service Bus removes once it has been idle for `InstanceSubscriptionIdleTimeout` (5 minutes, the minimum) when its process stops. Names longer than Service Bus allows (260 characters, 50 for subscriptions), or ending in `.`, `_` or `-`, are shortened and get a hash. Service Bus names are case-insensitive, so names that differ only in case share an entity.
- **Sending to one subscription** (dead-letter replays, retries): Service Bus cannot, so the message goes to the topic with `x-foundatio-to` naming the subscription, and every subscription the transport creates has the rule `foundatio`, matching messages without that property or with its own name. An existing subscription that only has the default rule gets this rule instead; one with rules of its own is left alone, with a warning.
- **Dead letters**: each entity's own dead-letter queue, with the reason (cut to 1,024 bytes) in `DeadLetterReason`. Messages Service Bus dead-letters itself (`MaxDeliveryCountExceeded`, expiry) are received with its reason and description.
- **Leases** are peek-lock locks. Their duration is the entity's `LockDuration` (set from the option when the transport creates it, at most 5 minutes), whatever `LeaseDuration` a handler asks for: each delivery reports `LeaseExpiresAt`, and Foundatio renews a third of the way through it.
- **Delays**: sends and publishes are scheduled messages, for any length of time. Service Bus cannot abandon with a delay, so a delayed retry schedules a copy of the message carrying its delivery count and completes the original: in one transaction on a queue, one after the other on a subscription (a transaction there would span the topic and the subscription), where a failure in between delivers the message twice. Retries under a second are held by Foundatio instead.
- **Groups**: with `RequiresSession = true`, every queue and subscription has sessions and a group key is the session id. A receiver accepts sessions as it needs them (up to `MaxConcurrentSessions` at a time) and receives no more of a session while a message it handed out of it is unsettled. A delayed message with a group key goes into its session at once with its due time, and the receiver that gets the session holds it, and the session, until it is due; a delayed retry is held the same way. Both keep the session locked, renewing it. Messages without a group key share 8 sessions, from which receivers take many at a time. Without sessions, a group key still travels as the session id, unordered.
- **Deduplication**: with `RequiresDuplicateDetection = true`, queues and topics detect duplicates within `DuplicateDetectionWindow` (10 minutes); the deduplication id is the Service Bus message id and the Foundatio message id travels in `x-foundatio-id`. A deduplication id sent to one subscription is hashed with its name, since the topic deduplicates for all of its subscriptions.
- **Headers** are application properties, so other clients can read them; names starting with `x-foundatio-` are refused. Headers that would take more than 48 KiB there (Service Bus allows 64 KiB of properties) travel at the start of the body instead.
- **Size**: `MaxMessageSize` (256 KiB, the Standard tier's limit; set it for Premium, up to 100 MiB) less 4 KiB, which the transport keeps for framing, its properties, a retry's copy and the dead-letter reason.
- **Deletion**: `DeleteAsync` deletes the queue or subscription (and so its dead letters); topics are kept. Deleting a dead-letter source receives and deletes the dead letters no receiver holds.
- Entities are created with the administration client. With `CreateEntities = false`, for applications without Manage rights, every queue, topic and subscription must exist (with the `foundatio` rule on subscriptions that should only get their own targeted messages), instance subscriptions are not available and only dead letters can be deleted. An existing entity must match the session mode; one that doesn't is refused.

Things to plan for:

- **Ungrouped messages in session mode** share 8 sessions, and one receiver holds a session at a time, so at most 8 receivers handle them at once.
- **Delayed grouped messages and grouped retries** each keep a session locked by the receiver that holds them until they are due, renewing its lock. A receiver holds up to 256 such sessions besides the ones it is handling, so use jobs, or delays without a group key, for long or high-volume delays. When the receiver closes, a held message is released without counting a delivery; a held retry counts one.
- **Lock duration**: a long lock means a crashed process's messages wait that long to be delivered again; a short one means more renewals. The default is 1 minute.
- **Max delivery count**: entities the transport creates have `MaxDeliveryCount = Int32.MaxValue` so Foundatio's retry policy decides when to dead-letter. Lower it only above the attempts your policies allow.

Monitoring: `GetStatsAsync` reads the entity's runtime properties, one administration call, and creates nothing.

| Count | On Service Bus |
|---|---|
| `Queued` | `ActiveMessageCount`. Service Bus counts a locked message as active, so this includes messages being handled, and in session mode those a receiver holds until due. |
| `Delayed` | `ScheduledMessageCount` of a queue: delayed sends and retries. Null for subscriptions, whose scheduled messages wait in the topic, which counts them for all its subscriptions together. |
| `InFlight` | Null: Service Bus does not report locked messages apart from active ones. |
| `DeadLettered` | `DeadLetterMessageCount`. |

- Instance subscriptions are not counted (`GetStatsAsync` returns null), nor is anything without an administration client.
- A source that does not exist counts as empty.
- The Service Bus emulator reports zero for every count, so on it (`UseDevelopmentEmulator=true`) `GetStatsAsync` returns null.

The transport has been tested against the [Service Bus emulator](https://learn.microsoft.com/azure/service-bus-messaging/overview-emulator) only. The emulator caps `MaxDeliveryCount` at 10, a namespace at 50 queues and a topic at 50 subscriptions, ignores `AutoDeleteOnIdle` on subscriptions, serves its management API on port 5300 (the transport uses it when the connection string has `UseDevelopmentEmulator=true`) and reports no counts.

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
dotnet add package Foundatio.AzureServiceBus --prerelease
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

[![contributors](https://contributors-img.web.app/image?repo=foundatiofx/Foundatio.AzureServiceBus)](https://github.com/foundatiofx/Foundatio.AzureServiceBus/graphs/contributors)
