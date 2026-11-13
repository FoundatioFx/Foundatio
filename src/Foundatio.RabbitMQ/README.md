![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.RabbitMQ.svg?style=flat)](https://www.nuget.org/packages/Foundatio.RabbitMQ/)
[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FFoundatio.RabbitMQ%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Foundatio.RabbitMQ/latest/download)
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
dotnet add package Foundatio.RabbitMQ
```

```csharp
// Messaging
IMessageBus messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString("amqp://localhost"));
await messageBus.PublishAsync(new MyMessage { Data = "Hello" });
```

### Message transport (preview)

`RabbitMQMessageTransport` plugs RabbitMQ 4 into Foundatio's newer messaging (`IMessageClient`, `AddConsumer`, `AddSubscriber`) and supports queues (competing consumers), topics with durable and per-instance subscriptions, dead letters, and durable delays without the archived delayed-message plugin. It has been tested on RabbitMQ 4.2 and 4.3.

```csharp
services.AddFoundatioMessaging(m => m
    .UseRabbitMQ(new RabbitMQMessageTransportOptions
    {
        ConnectionString = "amqp://guest:guest@localhost:5672",
        Prefix = "myapp",
        ManagementEndpoint = new Uri("http://localhost:15672")   // optional, to count in-flight messages
    }));
```

- `UseRabbitMQ("amqp://guest:guest@localhost:5672")` uses the default options.
- `UseRabbitMQ()`, or options without a `ConnectionString`, uses the `IConnection` registered in DI (Aspire's `AddRabbitMQClient` registers one), which stays the app's. `UseRabbitMQ(connection, options)` takes one explicitly.
- `UseRabbitMQ(sp => new RabbitMQMessageTransportOptions { ... })` builds the options from the service provider, for example from configuration.
- The service provider creates the transport and disposes it at shutdown. `LoggerFactory` and `TimeProvider` come from DI unless the options set them.

How it maps to RabbitMQ:

- **Queue**: a quorum queue `<prefix>.queue.<name>`. **Topic**: a fanout exchange `<prefix>.topic.<name>`. **Durable subscription**: a quorum queue `<prefix>.subscription.<topic>:<name>` bound to it; a new subscription gets messages published after it is created, and `MessageDestination.Subscription` sends (dead-letter replays) go straight to its queue. **Instance subscription**: an exclusive, auto-deleted classic queue bound to the exchange, which the broker deletes when the receiver closes or its connection is lost, so nothing is left behind by a crashed process.
- **Dead letters**: a quorum queue per source, `<prefix>.deadletter.queue.<name>` or `<prefix>.deadletter.subscription.<topic>:<name>`. Dead-lettering sends a copy with the reason (cut to 4 KiB) and then acknowledges the original; a dead letter released by a peek goes back to its place.
- **Sending**: every send and publish waits for publisher confirms. The queue, or the topic's exchange and the subscription's binding, is declared first, so nothing sent before a receiver exists is lost. A send to a queue another process deleted comes back as unroutable, and the transport declares it again and sends once more. A publish to a topic without subscriptions is dropped.
- **Leases**: a delivery stays with its receiver's channel until it is settled, so leases never expire (`LeaseExpiration = false`); renewing reports a delivery whose channel has closed. Unsettled deliveries go back to their queue when the receiver or its connection closes. A receiver whose channel closes, or whose queue is deleted, faults and Foundatio replaces it. RabbitMQ closes a channel that holds a delivery longer than `consumer_timeout` (30 minutes by default): raise it, for example with a `consumer-timeout` policy, for handlers that run longer, or use jobs.
- **Delivery counts and retries**: quorum queues are declared with `x-delivery-limit: -1`, so the broker never dead-letters or drops a message on its own; Foundatio decides. They count a delivery returned by a closed channel, but not one released with a requeue, so an abandoned message is sent to its queue again carrying its delivery count, and the original is then acknowledged. A crash between the two delivers it twice.
- **Delays**: 28 shared levels, `<prefix>.delay.00` to `<prefix>.delay.27`, each a topic exchange and a quorum queue that holds messages for 2^level seconds and then dead-letters them, at least once, to the next level; the routing key spells the delay in binary, and `<prefix>.delay` delivers by target name. Delayed sends, publishes and retries wait on the broker, survive restarts with no process running, and arrive within about a second after they are due (delays are whole seconds, rounded up; Foundatio holds retries under a second in-process). The longest delay is 2^28 - 1 seconds, about 8.5 years. A delayed message for a queue that was deleted, or a topic without subscriptions, goes to `<prefix>.discard`, a queue that keeps nothing, so it cannot hold up its level. `DelayedDelivery = false` declares none of this: Foundatio then holds retries in-process and rejects delayed sends.
- **Flow control**: each receiver has its own channel; the broker pushes deliveries into a buffer, up to a prefetch of twice the handler concurrency (Foundatio's `PrefetchCount`), the messages being handled plus as many ahead. A caller holding a whole prefetch of unsettled messages still receives: the receiver starts a fresh consumer and cancels the old one, whose deliveries can still be settled.
- **Headers**: application headers are AMQP headers with UTF-8 string values, so other clients see them; the id is the AMQP message id (or `x-foundatio-id` when longer than 255 bytes) and the timestamp is when it was sent. Names the broker or RabbitMQ.Client set or route by (`x-*`, `CC`, `BCC`, and `traceparent`, `tracestate` and `baggage`, which the client replaces while it is traced) travel as `x-foundatio-h-<name>`. Headers too large for the AMQP properties frame (more than 64 KiB, or a name over 255 bytes) move into an envelope at the start of the body. Messages from other producers are read as they are: their headers that don't start with `x-` become headers.
- **Size**: `MaxMessageBytes` (16 MiB) is the broker's `max_message_size`; messages may be that less 16 KiB as `MessageSize` measures them.
- **Connections**: the transport opens one connection to publish and declare on and one for its receivers, with automatic recovery off: it opens a new one when it next needs it, and Foundatio replaces the receivers of a lost one. `Hosts` lists hosts to fail over between, and `ConfigureConnectionFactory` adjusts the factory (TLS, heartbeats).
- **Provisioning**: exchanges, queues and bindings are declared on first use, always with the same arguments. One declared elsewhere with other arguments fails with `MessageBusException` naming it; delete it, or use another `Prefix`.
- **Deletion**: `DeleteAsync` deletes the source's queue and dead-letter queue (or only the dead-letter queue); receivers open on it, in any process, are cancelled by the broker and replaced. A subscription's topic exchange stays.

**Monitoring**: `GetStatsAsync(source)` counts a queue's or durable subscription's messages without declaring anything; a source that does not exist counts as empty, and an instance subscription returns `null`.

- Without `ManagementEndpoint`: `Queued` and `DeadLettered` are the ready messages of the queue and its dead-letter queue, from passive declares (one round trip each), current at the moment of the call. `InFlight` is `null`: AMQP does not report delivered, unacknowledged messages, and they are counted nowhere.
- With `ManagementEndpoint` (credentials from its URI, or else the connection string's): `Queued` is `messages_ready` and `InFlight` is `messages_unacknowledged`, which includes messages a receiver has buffered and not handed out yet; `DeadLettered` counts every message in the dead-letter queue, peeked ones too. The management API refreshes its counts every 5 seconds by default (`collect_statistics_interval`).
- `Delayed` is `null`: delayed messages and delayed retries wait in delay queues every source shares.

Limits: groups are not ordered and messages are not deduplicated; a crash between an abandon's or a dead-letter's copy and the original's acknowledgement delivers both; messages a receiver had buffered but not handed out count one more delivery when its connection is lost; delays have one-second resolution. The tests ran against a local broker started through Aspire.

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

### Delayed Message Delivery

Foundatio.RabbitMQ supports delayed message delivery via the `DeliveryDelay` option on `PublishAsync`.

**Current behavior:**

1. **RabbitMQ < 4.3 with plugin installed**: If the [`rabbitmq_delayed_message_exchange`](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/) plugin is detected, it is used for delayed delivery. A warning is logged because the plugin is deprecated and will not work on RabbitMQ 4.3+.
2. **RabbitMQ < 4.3 without plugin**: Falls back to an in-memory delay scheduler provided by `MessageBusBase`. Messages are held in process memory and delivered after the delay. **This is not durable** -- delayed messages are lost if the process restarts.
3. **When the RabbitMQ server version is detected as >= 4.3**: The plugin probe is skipped (the plugin depends on Mnesia, which was removed in 4.3), and the in-memory fallback is used automatically. If the server version cannot be determined from `ServerProperties["version"]`, the probe may still be attempted before falling back.

**Migration guidance:**

The `rabbitmq_delayed_message_exchange` plugin is [archived and no longer maintained](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/). RabbitMQ 4.3 removes Mnesia, making the plugin incompatible. If you rely on delayed messages:

- On RabbitMQ < 4.3: The plugin still works but logs a deprecation warning at startup.
- On RabbitMQ >= 4.3: Delayed messages use the in-memory fallback automatically. Be aware that these are not durable across process restarts.
- For durable delayed delivery on RabbitMQ 4.3+, use the [message transport](#message-transport-preview), whose delays wait in delay queues on the broker, or an external scheduler.

### RabbitMQ 4.3 Feature Support

**Supported (AMQP 0.9.1 compatible):**

- 32 strict message priority levels on quorum queues (via `UseMessagePriority()`)
- Delayed retries with linear backoff (via `UseDelayedRetries()`)
- Per-queue consumer timeouts (via `ConsumerTimeout()`)
- Single active consumer (via `UseSingleActiveConsumer()`)

**Not supported (require AMQP 1.0 protocol):**

- `x-opt-delivery-time` annotation -- per-message delayed retry override via the `modified` disposition outcome. AMQP 0.9.1 `basic.nack`/`basic.reject` do not support annotations.
- `x-opt-delivery-delay` annotation -- relative delay for the enterprise Message Scheduler / Delayed Queue plugin.
- Rejected-by and rejection reason -- returned to publishers in the AMQP 1.0 `Rejected` outcome.
- Consumer activity notification -- signaled via AMQP 1.0 flow frames for single active consumer state transitions.

This library uses the `RabbitMQ.Client` package (AMQP 0.9.1). To use AMQP 1.0 features, consider the [Amqp.Net Lite](https://github.com/Azure/amqpnetlite) library or the [RabbitMQ AMQP 1.0 .NET client](https://github.com/rabbitmq/rabbitmq-amqp-dotnet-client).

### OpenTelemetry

Foundatio automatically propagates W3C trace context (`traceparent` / `tracestate`) through message headers. On publish, the current `Activity.Id` is stored as the message's `CorrelationId`; on receive, Foundatio starts a new `Activity` parented to that ID, linking consumer spans back to the publisher's trace.

To collect Foundatio's application-level message spans, add the `"Foundatio"` source:

```csharp
.AddSource("Foundatio")
```

For additional **transport-level** visibility (AMQP channel operations, network round-trips), the `RabbitMQ.Client` 7.x library emits its own spans:

```csharp
.AddSource("RabbitMQ.Client.*")
```

A complete tracing setup:

```csharp
services.AddOpenTelemetry().WithTracing(tracing =>
{
    tracing.AddSource("Foundatio");          // message publish/handle spans
    tracing.AddSource("RabbitMQ.Client.*");  // AMQP transport spans (optional)
    tracing.AddOtlpExporter();
});
```

> **Note:** The [`RabbitMQ.Client.OpenTelemetry`](https://www.nuget.org/packages/RabbitMQ.Client.OpenTelemetry/) package (currently pre-release) is NOT required -- Foundatio handles cross-process trace propagation at the application level. That package adds an alternative propagation mechanism at the transport level which is redundant when using Foundatio.

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
dotnet add package Foundatio.RabbitMQ --prerelease
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

[![contributors](https://contributors-img.web.app/image?repo=foundatiofx/Foundatio.RabbitMQ)](https://github.com/foundatiofx/Foundatio.RabbitMQ/graphs/contributors)
