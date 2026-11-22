---
head:
  - - script
    - {}
    - "addEventListener('DOMContentLoaded', () => { const id = decodeURIComponent(location.hash.slice(1)); if (id && !document.getElementById(id)) location.replace('/guide/message-bus' + location.hash) })"
---

# Messaging

Send commands to one consumer, or publish events to every interested service. Handlers are plain classes resolved from DI; Foundatio takes care of serialization, retries, dead letters, concurrency, lease renewal and graceful shutdown.

> This replaces `IQueue<T>` and `IMessageBus` for new code. Both remain available, documented under [Queues (legacy)](./queues) and [Message Bus (legacy)](./message-bus); see [Migrating](./migrating-to-messaging) to move existing code, or to [use both APIs side by side](./migrating-to-messaging#mixing-imessagebus-and-imessageclient).

## Quick start

```csharp
// Contracts: plain records with stable names
[Message("orders.order-placed")] public record OrderPlaced(int OrderId, string Email);
[Message("orders.send-receipt")] public record SendReceipt(int OrderId, string Email);

// Handlers
public class OrderPlacedHandler(IMessageClient messages) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced order, IMessageContext context, CancellationToken ct)
        => messages.SendAsync(new SendReceipt(order.OrderId, order.Email), cancellationToken: ct);
}

public class SendReceiptHandler(IEmailSender email) : IMessageHandler<SendReceipt>
{
    public Task HandleAsync(SendReceipt receipt, IMessageContext context, CancellationToken ct)
        => email.SendReceiptAsync(receipt.Email, receipt.OrderId, ct);
}

// Registration
builder.Services.AddFoundatioMessaging(m => m
    .UseServiceName("orders")             // names this service's subscriptions
    .UseInMemory()                        // or UseRedis(), UseSqs(), UseKafka(...), UseAzureServiceBus(...), UseAzureStorageQueues(...) or UseRabbitMQ(...); see Transports
    .AddSubscriber<OrderPlacedHandler>()  // events
    .AddConsumer<SendReceiptHandler>());  // commands

// Anywhere
app.MapPost("/orders", (IMessageClient messages) => messages.PublishAsync(new OrderPlaced(1, "a@b.com")));
```

Registered handlers start receiving when the host starts. A process that registers no handlers only sends.

## Transports

`UseInMemory()` keeps messages in the process, which suits tests and a single instance. To run several instances, use a broker. Each provider package adds a `Use...` method to the builder, with no extra `using`; the options types are in `Foundatio.Messaging.Transport`.

- The service provider creates the transport and disposes it when the host shuts down.
- `LoggerFactory` and `TimeProvider` come from the service provider unless the options set them.
- Each method takes the options, or a `Func<IServiceProvider, TOptions>` that builds them, for example from configuration.
- An app has one transport for all its queues and topics, and `AddLegacyMessageBus()` uses it too. The last `Use...` call wins.
- `AddFoundatioMessaging` calls add up, so a shared library can choose the transport and map message names while each process adds its own handlers. To choose the transport from configuration, branch in your code: `if (useRedis) m.UseRedis(); else m.UseInMemory();`.
- Bodies are serialized with `MessagingOptions.Serializer`, or the `ISerializer` registered in DI, or a JSON serializer when neither is set. Services that share a queue or topic must use compatible serializers.

### Using Redis

`dotnet add package Foundatio.Redis`. The transport uses Redis Streams. It needs Redis 6.2 or later, or Valkey, on a single server, Sentinel or a cluster.

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(await ConnectionMultiplexer.ConnectAsync("localhost"));
builder.Services.AddFoundatioMessaging(m => m
    .UseRedis(new RedisMessageTransportOptions { Prefix = "myapp:messaging" })   // or UseRedis() for the defaults
    .UseServiceName("billing")
    .AddConsumer<SendReceiptHandler>());
```

- `UseRedis()` uses the `IConnectionMultiplexer` registered in DI, and the host fails to start without one. To use another connection, call `UseRedis(connection, options)`. Either way the connection stays yours; the transport doesn't close it.
- `UseRedis(builder.Configuration.GetConnectionString("Redis")!)` connects for you instead: it registers the `IConnectionMultiplexer`, which jobs (`UseRedis` with the same string), caches and locks can share, and the host disposes it.
- It orders groups, honors `DeduplicationId`, and delays messages for any length of time.
- Messages live in Redis memory. Set `maxmemory-policy noeviction`, and bound dead letters with `DeadLetterMaxLength` or `DeadLetterRetention`.
- See the [Redis README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Redis#redis-message-transport) for every option, memory use and latency.

### Using SQS

`dotnet add package Foundatio.AWS`. Queues are SQS queues; topics are SNS topics, and each subscription is an SQS queue subscribed to its topic.

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseSqs(new SqsMessageTransportOptions { ConnectionString = "Region=us-east-1", Prefix = "prod-" })
    .UseServiceName("billing")
    .AddConsumer<SendReceiptHandler>());

// Or build the options from configuration
m.UseSqs(sp => new SqsMessageTransportOptions
{
    ConnectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Sqs"),
    Prefix = "prod-"
});
```

- `UseSqs("Region=us-east-1")` takes the connection string alone. Without one (`UseSqs()`), the `IAmazonSQS` and `IAmazonSimpleNotificationService` registered in DI are used (`AddAWSService<IAmazonSQS>()`), or, with neither registered, the default AWS credential and region resolution, such as an IAM role. `UseSqs(sqs, sns, options)` takes the clients.
- Standard queues neither order groups nor deduplicate. Set `Fifo = true` for both, at lower throughput.
- An empty `Prefix` shares queue names with `SQSQueue<T>` and `SQSMessageBus`. Use a prefix while they run side by side.
- See the [AWS README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AWS#message-transport-preview) for delays, quotas and the IAM actions the transport needs.

### Using Kafka

`dotnet add package Foundatio.Kafka`. Queues and topics are Kafka topics, and each queue and durable subscription reads its topic as a consumer group.

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseKafka("localhost:9092")
    .UseServiceName("billing")
    .AddConsumer<SendReceiptHandler>());

// Or with options
m.UseKafka(new KafkaMessageTransportOptions { BootstrapServers = "broker:9092", Partitions = 6, ReplicationFactor = 3 });
```

- It orders groups and delays messages through delay topics. It doesn't deduplicate, and leases never expire.
- Competing instances share a topic's partitions, so create topics with at least as many partitions as you run instances.
- A consumer that crashes keeps its partitions until the group's session timeout ends (45 seconds by default), so their messages wait that long; the README says how to shorten it.
- See the [Kafka README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.Kafka#message-transport-preview) for how it maps to Kafka, retention and limits.

### Using Azure Service Bus

`dotnet add package Foundatio.AzureServiceBus`. Queues are Service Bus queues, topics are topics, and each subscription is a topic subscription.

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseAzureServiceBus(builder.Configuration.GetConnectionString("ServiceBus")!)
    .UseServiceName("billing")
    .AddConsumer<SendReceiptHandler>());

// Or with options, for example Microsoft Entra ID and sessions
m.UseAzureServiceBus(new AzureServiceBusMessageTransportOptions
{
    FullyQualifiedNamespace = "my-namespace.servicebus.windows.net",
    Credential = new DefaultAzureCredential(),
    RequiresSession = true
});
```

- Without a connection in the options (`UseAzureServiceBus()`), it uses the `ServiceBusClient` and `ServiceBusAdministrationClient` registered in DI, which stay yours.
- Without options it neither orders groups nor deduplicates. `RequiresSession = true` orders groups with sessions and `RequiresDuplicateDetection = true` deduplicates; entities must be created in the same mode.
- Delays are scheduled messages, for any length of time. Leases follow each entity's lock duration (1 minute by default, at most 5), renewed by Foundatio.
- The transport creates queues, topics and subscriptions as it needs them. Set `CreateEntities = false` when they are provisioned for you.
- See the [Azure Service Bus README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AzureServiceBus#message-transport-preview) for names, sessions, sizes and the emulator.

### Using Azure Storage Queues

`dotnet add package Foundatio.AzureStorage`. Queues are Storage queues. Storage has no topics, so each durable subscription is a queue of its own, and a publish sends a copy to each of its topic's subscription queues.

```csharp
builder.Services.AddFoundatioMessaging(m => m
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

- Without a connection in the options (`UseAzureStorageQueues()`), it uses the `QueueServiceClient` registered in DI, which stays yours.
- It doesn't order groups or deduplicate. Delays and leases are visibility timeouts, of up to 7 days. Messages never expire unless you set `MessageTimeToLive`.
- Storage can't long-poll, so receivers poll, backing off to `MaxPollInterval` (2 seconds) while the queue is empty: a message sent from another process can wait that long, and each idle receiver makes a billed request every 2 seconds. Sends through the same process wake its receivers at once.
- Messages can be 46 KiB with their headers; store larger payloads with `IFileStorage` and send a reference. Dead letters go to a `-poison` queue.
- See the [Azure Storage README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.AzureStorage#message-transport-preview) for names, polling cost, counts and Azurite.

### Using RabbitMQ

`dotnet add package Foundatio.RabbitMQ`. It needs RabbitMQ 4. Queues and subscriptions are quorum queues; topics are fanout exchanges, and each durable subscription is a queue bound to its topic's exchange.

```csharp
builder.Services.AddFoundatioMessaging(m => m
    .UseRabbitMQ("amqp://guest:guest@localhost:5672")
    .UseServiceName("billing")
    .AddConsumer<SendReceiptHandler>());

// Or with options, on the IConnection registered in DI (for example by Aspire's AddRabbitMQClient)
m.UseRabbitMQ(new RabbitMQMessageTransportOptions { Prefix = "myapp" });
```

- Without a `ConnectionString`, `UseRabbitMQ` uses the `IConnection` registered in DI, and the host fails to start without one; that connection stays yours. With one, the transport opens and closes its own connections.
- It delays messages for up to about 8.5 years, in whole seconds, through delay queues on the broker that need no plugin and survive restarts. It doesn't order groups or deduplicate, and leases never expire.
- A handler that holds a message longer than the broker's `consumer_timeout` (30 minutes by default) loses its channel, and the message is delivered again; raise it for long handlers, or use jobs.
- Sends wait for the broker's confirms for at most `PublishTimeout` (30 seconds), then throw `MessageBusException`. `Hosts` lists nodes to fail over between. On a cluster, set `cluster_partition_handling = pause_minority`: with the default, a node cut off from the others keeps its clients, whose sends time out and whose receivers get nothing for about a minute.
- See the [RabbitMQ README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.RabbitMQ#message-transport-preview) for how it maps to RabbitMQ, headers, counts and limits.

## Send or publish?

| | `SendAsync` (command) | `PublishAsync` (event) |
|---|---|---|
| Who gets it | One consumer | Every subscribing service |
| Register with | `AddConsumer<THandler>()` | `AddSubscriber<THandler>()` |
| Replicas of one service | Compete | Compete for the service's copy |
| While no one is listening | Waits in the queue | Copies wait in each existing subscription; with no subscriptions it is dropped |

`AddConsumer<THandler>()` and `AddSubscriber<THandler>()` register every `IMessageHandler<T>` the class implements. For a class that handles a command and subscribes to an event, name the message type, so the command gets only a queue and the event only a subscription:

```csharp
m.AddConsumer<OrderWorkflowHandler, PlaceOrder>()
 .AddSubscriber<OrderWorkflowHandler, OrderShipped>();
```

Mixing them up delivers nothing, and only your app's own handlers are known to it: the first time a type is sent that your app only subscribes to, or published that it only consumes, a warning is logged suggesting the other call. A mix-up between services is not detected.

For "every running instance" (local caches, pushing to WebSocket clients) use an instance-scoped subscriber:

```csharp
m.AddSubscriber<InvalidateLocalCache>(new SubscriptionOptions { Scope = SubscriptionScope.Instance });

// or at runtime, until disposed
await using var subscription = await messages.SubscribeAsync<OrderPlaced>((order, context, ct) => hub.NotifyAsync(order, ct));
```

### Service names and shared subscriptions

A service's subscription to a topic is named after the service, set with `m.UseServiceName("billing")`. The broker keeps the subscription under that name, so choose it deliberately: two services with the same name split each other's events, and renaming a service starts new subscriptions that receive events published from then on, leaving the old ones' backlog behind. On every transport except the in-memory one, the host fails to start when a service-scoped subscriber has no name to use. Only the in-memory transport, and so the test harness, defaults it to the host's application name.

All subscribers of one topic in a service share that subscription, so they share each delivery:

- If one handler throws, every handler of that event runs again on the retry, so keep them all idempotent.
- They run with one `RetryPolicy`, `LeaseDuration` and `MaxConcurrency`. The host fails to start when handlers that share a subscription set different values. A value set on one handler and left unset on the other counts as different. Policies compare by value: two built the same way with `RetryPolicy.Exponential`, `Fixed` and `DoNotRetry` are equal, but two with `Delay` or `ShouldRetry` delegates you wrote yourself differ unless they share the instance.

To retry a handler on its own, with its own settings, give it a subscription of its own:

```csharp
m.AddSubscriber<SendWelcomeEmail>()   // subscription "billing"
 .AddSubscriber<SyncToCrm>(new SubscriptionOptions { Name = "billing-crm", RetryPolicy = RetryPolicy.None });
```

Moving a handler to a new subscription name, or renaming one, starts that subscription with events published from then on: events still waiting in the old subscription reach only the handlers left on it. A subscription that no handler uses any more keeps collecting copies until you delete it with `IMessageAdmin.DeleteAsync`. The same rules apply to instance subscribers of one topic, and to consumers of message types that you route to one queue.

## Handling outcomes

- **Return** to complete the message.
- **Throw** to retry. The default policy makes 5 attempts, waiting about 1s, 4s, 16s and 64s; then the message is dead-lettered.
- **Decide without throwing**: `context.DeadLetter("invalid sku")` or `context.Retry(TimeSpan.FromMinutes(1))`.

```csharp
m.AddConsumer<ImportHandler>(new HandlerOptions
{
    MaxConcurrency = 4,                                  // per instance; default: processor count
    LeaseDuration = TimeSpan.FromMinutes(5),             // renewed automatically while handling
    RetryPolicy = RetryPolicy.Default.DoNotRetry<ValidationException>()
});
```

Build a policy's delays with `RetryPolicy.Exponential(initial, max)` (four times longer after each attempt, ±20% jitter; the default is `Exponential(1s, 10min)`), `RetryPolicy.Fixed(delay)`, or your own `Func<int, TimeSpan>` of the failed attempt (1-based): `new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromSeconds(30)) }`. `MaxAttempts` counts the first attempt. A handler without a `RetryPolicy` uses `MessagingOptions.DefaultRetryPolicy`, which you set with `m.Configure(o => o.DefaultRetryPolicy = ...)`.

A retry waits on the broker when it can delay it. When it can't (Kafka or RabbitMQ without delayed delivery, or a delay under the transport's `MinRetryDelay`, such as SQS's whole seconds), Foundatio holds the message in-process for the backoff. The message stays leased, and other messages use its handler slot meanwhile. At most 10 × `MaxConcurrency` messages per handler are held that way; further retries keep their slots until the backoff ends. A delay within 20% of `MinRetryDelay` is lengthened to it instead, so the default policy's first retry (about 1s) waits on the broker. Stopping the host releases held messages at once.

Settling doesn't keep a handler slot waiting on the broker: once a handler returns, the complete, abandon or dead-letter runs while the slot takes the next message. The message stays in flight until the settlement finishes, and only then is its outcome reported and shutdown done waiting for it. Up to 2 × `MaxConcurrency` settlements per handler run that way; past that a settlement keeps its slot until it finishes. In an ordered group, the group's next message still waits for the previous one to settle.

On SQS, Redis and Service Bus without sessions, receiving doesn't wait for a free handler slot either: while the handlers are busy, Foundatio receives full batches ahead of them, and the messages wait, leased and renewed, until a slot frees. How far it reads ahead adapts to how long your handlers and the broker's receives take, so a buffered message normally waits less than a second (or a quarter of the lease, if that's shorter); it doesn't read ahead for handlers that take longer than that, and reads ahead only as many messages as keep the buffered bodies within 16 MiB at their recent size. While the source is drained, only one receive long-polls. Buffered messages count as unsettled on the broker (in flight on SQS and Redis, queued on Service Bus), show in `MessagingStatus` as `Buffered` rather than `Active`, and hold back their group's later messages. Stopping the host handles them within the shutdown drain; any still buffered when it times out, or when a failed receiver is replaced, are released without running their handlers and delivered again.

To read handler options from configuration bound after your registrations, pass a factory: `m.AddConsumer<ImportHandler>(sp => new HandlerOptions { MaxConcurrency = sp.GetRequiredService<IOptions<ImportSettings>>().Value.Workers })`, or `AddSubscriber<THandler>(sp => new SubscriptionOptions { ... })`. It runs when the host starts. A consumer has no subscription, so passing `SubscriptionOptions` with a `Name` or `Scope` to `AddConsumer` throws.

`MessagingOptions`, which `m.Configure(...)` changes, is a standard options class: `services.Configure<MessagingOptions>(...)` and binding it from configuration work too, applied in the order they were registered.

Stopping the host doesn't cancel handlers at once. As soon as the host begins stopping, every handler stops receiving, and the job worker stops claiming, so nothing new starts during shutdown. Foundatio then waits up to `MessagingOptions.ShutdownTimeout` (20 seconds, set with `m.Configure(o => o.ShutdownTimeout = ...)`) for in-flight messages to finish, then fires their cancellation token and gives the handlers 5 more seconds; those messages are delivered again. Messaging and jobs drain at the same time, so keep each `ShutdownTimeout` plus 5 seconds under the host's `HostOptions.ShutdownTimeout` (30 seconds by default) and your orchestrator's grace period (30 seconds on Kubernetes).

Delivery is at least once: a crash or lost lease delivers a message again, so make handlers idempotent. A message is completed only after its handler returns, so a crash after a side effect and before the completion repeats the side effect too.

There is no transactional outbox: a send or publish is not part of your database transaction. Send after the transaction commits, and make handlers tolerate a message whose data was rolled back or not yet visible.

When the broker can't be reached to renew a lease (SQS, Redis, Service Bus), Foundatio cancels the handler's `CancellationToken` shortly before the lease ends (a quarter of the lease before, at most 5 seconds) and leaves the message unsettled, as the job worker does with a job's lease, so it doesn't keep running while the broker gives the message to another consumer. Pass the token on to whatever the handler awaits. Kafka and RabbitMQ leases don't expire, so failed renewals there never cancel a handler.

## Reporting progress

A long-running handler can report how far it has got, so it shows in the [monitor](./monitoring) with its progress:

```csharp
public class ImportCatalogHandler(ICatalog catalog) : IMessageHandler<ImportCatalog>
{
    public async Task HandleAsync(ImportCatalog message, IMessageContext context, CancellationToken ct)
    {
        var pages = await catalog.GetPagesAsync(message.Url, ct);
        for (int i = 0; i < pages.Count; i++)
        {
            await catalog.ImportAsync(pages[i], ct);
            await context.ReportProgressAsync((i + 1) * 100 / pages.Count, $"Page {i + 1} of {pages.Count}", ct);
        }
    }
}
```

- Percent is 0 to 100, or null to keep the last value; anything else throws `ArgumentOutOfRangeException`. A null message keeps the last message too.
- A handler that reports progress shows as active at once, even if it finishes in a few milliseconds. One that doesn't shows after it has run for a second.
- Each report updates the monitor, adds a `progress` event to the handler's trace span and logs at Debug. Events and logs are throttled to about four a second, apart from 100 percent, so calling it in a tight loop is cheap.
- It never calls the transport. The message's lease is renewed automatically while the handler runs, whether or not it reports progress.

`ReportProgressAsync`, `Attempt`, `CorrelationId` and `Headers` come from `IHandlerContext` in the `Foundatio` namespace, which [jobs](./durable-jobs#reporting-progress) share, so a helper that takes an `IHandlerContext` works for both.

## Dead letters

Every queue and durable subscription has a dead-letter queue. Count, inspect and replay it with `IMessageAdmin`, on any transport. The admin names sources by message type, following your routing (`[Message]`, `MapMessage`, the service name), so you don't build the names by hand:

```csharp
var source = admin.GetSubscription<OrderPlaced>();   // this service's subscription; GetSubscription<OrderPlaced>("audit") for another, GetQueue<SendReceipt>() for a queue
IReadOnlyList<MessageSource> mine = admin.GetSources();   // every queue and durable subscription this app's handlers receive from

MessageSourceStats? counts = await admin.GetStatsAsync(source);   // Queued, Delayed, InFlight, DeadLettered; null where the transport can't tell
IReadOnlyList<DeadLetterMessage> failed = await admin.PeekDeadLettersAsync(source);   // Reason, MessageType, Message (deserialized), Headers
var nextPage = await admin.PeekDeadLettersAsync(source, skip: 20, max: 20);
int replayed = await admin.ReplayDeadLettersAsync(source);   // back to that queue or subscription only
bool found = await admin.ReplayDeadLetterAsync(source, failed[0].MessageId);   // just the one you fixed
await admin.DeleteDeadLetterAsync(source, poisonMessageId);   // drop one that should never run
await admin.PurgeDeadLettersAsync(source);
await admin.DeleteAsync(MessageSource.Subscription("orders.order-placed", "retired-service"));   // stop collecting copies for a removed service
```

Replayed messages keep their id, body and headers and are handled as new deliveries; the dead-letter reason, delay and expiry are dropped.

Peeking receives the messages and releases them, so a page after `skip` costs `skip + max` receives, and pages keep the dead-letter queue's order on every transport but SQS, where they may overlap. `ReplayDeadLetterAsync` and `DeleteDeadLetterAsync` look through up to 10,000 dead letters for the id and return false when it isn't there. Kafka records progress by offset and can't remove one message from the middle of a topic, so on Kafka they throw `MessageBusException`; replay or purge the whole dead-letter queue instead.

Dead-lettering is at least once, like delivery: on transports that move a message by copying it to the dead-letter queue and then removing the original (RabbitMQ, SQS and Kafka), a connection or lease lost in between delivers the original again, and it can be dead-lettered a second time. A dead-letter queue may then hold two entries with the same message id, which a replay sends twice; replayed handlers should be idempotent too.

## Options when sending

`SendAsync` and `PublishAsync` return the message id: `MessageId` when you set it, otherwise the generated one. The handler sees the same id in `context.MessageId`.

```csharp
string messageId = await messages.SendAsync(new SendReceipt(1, "a@b.com"), new SendOptions
{
    Delay = TimeSpan.FromMinutes(10),   // or DeliverAt, not both
    TimeToLive = TimeSpan.FromHours(1), // discard if not handled by then
    GroupKey = "customer-42",           // ordered delivery where the transport supports it (ids and keys: up to 128 characters)
    DeduplicationId = "receipt-1",      // dropped if seen recently, where the transport supports it
    CorrelationId = "order-1",          // defaults to the message or job being handled
    MessageId = "receipt-1",            // your own id, which handlers see as context.MessageId; generated otherwise
});
```

Application `Headers` can't reuse a name in `KnownHeaders` (`correlation-id`, `sent-at` and the others Foundatio writes); sending one throws `ArgumentException`.

Delays longer than the transport supports are handled automatically for sends. A publish delay beyond the transport's `MaxPublishDelay` throws, because re-sending it would deliver duplicates to every subscriber; send a command or schedule a job instead. A message larger than the transport allows throws `MessageTooLargeException` before anything is sent. `SendBatchAsync` and `PublishBatchAsync` send many messages in as few broker calls as possible.

### Batches

`SendBatchAsync` and `PublishBatchAsync` return the message ids in the order of the messages. `SendOptions.MessageId` and `DeduplicationId` name one message, so a batch rejects them; pass a selector instead to give each message its own ids and group key:

```csharp
IReadOnlyList<string> ids = await messages.SendBatchAsync(receipts, r => new MessageKeys
{
    MessageId = $"receipt-{r.OrderId}",
    DeduplicationId = $"receipt-{r.OrderId}",
    GroupKey = $"customer-{r.CustomerId}",   // null falls back to SendOptions.GroupKey
});
```

A batch that throws may have been partly sent: the messages before the failure are on their way. Sending the batch again delivers those twice unless each message keeps its id across the retry, as above; on a transport that deduplicates (Redis, SQS FIFO, Service Bus with duplicate detection), the `DeduplicationId` then drops the copies, and elsewhere handlers see the repeated `MessageId`.

`GroupKey` and `DeduplicationId` only guarantee something on transports that declare `OrderedGroups` and `Deduplication` in their `TransportCapabilities`. On other transports the message is still sent, and Foundatio logs a warning once per message type:

- Without ordered groups, messages of one group may be handled concurrently and out of order. SQS standard queues still send the key as the `MessageGroupId` of fair queues.
- Without deduplication (Kafka, RabbitMQ and SQS standard queues), a repeated `DeduplicationId` is delivered again.

To throw `MessageBusException` instead of sending, set `m.Configure(o => o.RequireTransportCapabilities = true)`.

With `GroupKey` on a transport that orders groups, messages of one group are handled one at a time, in order. When one is retried, the group's later messages wait for it, and each of them counts that as a delivery attempt. On SQS FIFO, a long-delayed message at the head of a group also adds an attempt to the messages behind it for about every 11 hours of its delay.

## Message names and routing

A message's name identifies it on the wire. By default it is the type's full name; set a stable one with `[Message("orders.order-placed")]` or `m.MapMessage<OrderPlaced>("orders.order-placed")`. By default a command goes to a queue, and an event to a topic, named after the message. Several types can share a topic:

```csharp
[Message("orders.shipped", Topic = "orders")] public record OrderShipped(int OrderId);
[Message("orders.cancelled", Topic = "orders")] public record OrderCancelled(int OrderId);
```

A command goes to its own queue the same way: `[Message("orders.send-receipt", Queue = "orders.commands")]`, or `m.MapMessage<SendReceipt>("orders.send-receipt", queue: "orders.commands")`. A `MessageSource` for [dead letters](#dead-letters) and stats takes those names: `MessageSource.Queue` takes the queue name, which is the message name unless you set one, and `MessageSource.Subscription` takes the topic and the subscription name (the service name unless the subscriber sets `SubscriptionOptions.Name`).

A subscriber ignores types on its topic that it has no handler for. A handler for a base type or interface also receives derived types:

- **Derived types in the base type's assembly** arrive as themselves, with all their fields, even from another service. At startup Foundatio learns every concrete type in the assembly of each handled type, so keep events next to their base type in a contracts assembly both services share. A type whose name another type already has, such as one you mapped explicitly, is left out.
- **Derived types this service can't load,** defined only in the publisher, arrive as the most derived concrete base type the handler accepts, without the fields that type lacks.
- **When every base type the handler accepts is abstract or an interface,** there is nothing to deserialize such a message as. On a topic the handler misses it, and Foundatio logs a warning once per type; on a queue it is retried, then dead-lettered. Move the type into the base type's assembly, or map a concrete type to its name with `MapMessage<T>("name")`.

## Testing

Unit test a handler with `TestMessageContext`. It records the outcome the handler chose and the progress it reported:

```csharp
var context = new TestMessageContext();
await new SendReceiptHandler(fakeEmail).HandleAsync(new SendReceipt(1, "a@b.com"), context, CancellationToken.None);
Assert.Null(context.DeadLetterReason);
Assert.Empty(context.ProgressReports);   // every report, in order; Progress and ProgressMessage hold the last values
```

To test your real registrations end to end, add `AddFoundatioTestHarness()` (namespace `Foundatio.Testing`), before or after them. It runs your messaging and jobs on an in-memory transport and job store and records what happens, so a test acts, waits for the outcome and asserts, with no sleeps. It owns the transport and store whatever the order: a `UseRedis()`, `UseSqs()` or `UseTransport()` call, yours or a provider's, is ignored while the harness is registered, so a test never reaches a real broker. The app needs only the `Foundatio` package for this; the `Foundatio.TestHarness` package holds the conformance suites for transport authors.

```csharp
var builder = Host.CreateApplicationBuilder();
builder.Services.AddOrdersApp();               // your AddFoundatioMessaging and AddFoundatioJobs calls
builder.Services.AddFoundatioTestHarness(o => o.RetryDelay = TimeSpan.FromMilliseconds(10));
using var host = builder.Build();
await host.StartAsync();
var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
var messages = host.Services.GetRequiredService<IMessageClient>();

await messages.PublishAsync(new OrderPlaced(1, "a@b.com"));
await harness.WaitForIdleAsync();

Assert.Equal(1, Assert.Single(harness.Sent<SendReceipt>()).OrderId);
Assert.All(harness.HandledMessages, m => Assert.Equal(MessageOutcome.Completed, m.Outcome));

await messages.SendAsync(new SendReceipt(2, "bounce@b.com"));
var deadLetter = Assert.Single(await harness.WaitForDeadLetteredAsync<SendReceipt>());
Assert.Equal(5, deadLetter.Attempt);           // the default policy's attempts, 10 ms apart
```

- `SentMessages`, `PublishedMessages` and `HandledMessages` record each message with its id, correlation id, attempt and headers. Deliveries also record how they ended (`Completed`, `Retried`, `DeadLettered`, `Skipped`, `Expired` or `Released`), the error, the handlers that ran to completion (`Handlers`), and the last `Progress` and `ProgressMessage` the handler reported. `Sent<T>()` and `Published<T>()` return the messages themselves.
- `WaitForHandledAsync<T>(count)` and `WaitForDeadLetteredAsync<T>(count)` return the deliveries once there are enough. They count deliveries of `T` to each queue or subscription, so two handlers that share a subscription count once; `WaitForHandledByAsync<THandler>(count)` and `HandledBy<THandler>()` count one handler's. On timeout (30 seconds by default) the exception says what happened instead, including "did you mean `PublishAsync`?" when an event was sent to its subscribers.
- `WaitForIdleAsync()` waits until no message is waiting for or being handled by your handlers, and no job is queued or running. Messages sent to queues your app does not consume, such as another service's commands, are ignored.
- `Reset()` forgets everything recorded, so counts and waits start again. When tests share a host, call `WaitForIdleAsync()` and then `Reset()` at the start of each test.

### Clocks and retries

The harness runs messaging, jobs, its transport and its store on the system clock, whatever `TimeProvider` your app registers, so a test that moves the app's clock doesn't leave messages leased or retries waiting for the time it moved. Waits always use real time.

- `o.RetryDelay` replaces the delay of every retry policy: the defaults and each handler's and job's own, so a retried message or job runs again after that delay instead of its production backoff. Attempts and `ShouldRetry` are kept.
- `o.TimeProvider = fakeTimeProvider` runs them on a clock the test moves instead: advance it to make delayed messages, delayed jobs and retries due.
- Outside the harness, `MessagingOptions.TimeProvider` and `JobWorkerOptions.TimeProvider` (set with `m.Configure(...)` and `j.Configure(...)`) do the same for the app: they default to the registered `TimeProvider`.

### Running work step by step

With processing paused, messages and jobs wait until the test runs them, so it can check what is queued before it is handled, and run a pipeline one step at a time. The work runs through the real dispatcher and worker, so retries, dead letters and settlement behave as in production. This replaces calling a legacy job's `RunAsync()` or `RunUntilEmptyAsync()` in a test.

```csharp
builder.Services.AddFoundatioTestHarness(o =>
{
    o.StartPaused = true;                // or call harness.PauseProcessingAsync() later
    o.RetryDelay = TimeSpan.Zero;        // retries and dead letters run within one ProcessPendingAsync call
});

await messages.PublishAsync(new OrderPlaced(1, "a@b.com"));
Assert.Empty(harness.HandledMessages);   // queued, not handled yet

var placed = await harness.ProcessPendingAsync<OrderPlaced>();        // only OrderPlaced's subscription
Assert.Single(harness.Sent<SendReceipt>());                          // sent, still waiting

await harness.ProcessPendingAsync<SendReceipt>();                     // the receipt queue
var rest = await harness.ProcessPendingAsync();                       // everything else that is due, jobs included
Assert.Equal(JobState.Succeeded, Assert.Single(rest.JobRuns).State);
```

- `ProcessPendingAsync()` runs what is due until nothing is left, including what the handlers send and enqueue, and returns the deliveries and job runs it saw. `ProcessPendingAsync<T>()` runs only the queues and subscriptions that deliver `T` to your handlers (everything due in them), and the jobs that take `T` as arguments or are the job class `T`; what they send elsewhere stays pending.
- Delayed messages and jobs, and retries waiting out their delay, are not due: set `RetryDelay`, or advance a fake `TimeProvider`, then call it again.
- While paused, receivers stay open, so subscriptions, instance subscriptions included, keep receiving what is published. `WaitForIdleAsync()` times out, saying processing is paused. `ResumeProcessing()` lets everything run on its own again.

### Several hosts on one transport

To test several instances of an app, or several services, against one broker, give each host's harness the same transport and store. Each harness records what its host does:

```csharp
await using var transport = new InMemoryMessageTransport();
var store = new InMemoryJobStore();
// for each host
builder.Services.AddFoundatioTestHarness(o => { o.Transport = transport; o.JobStore = store; });
```

You create and dispose them, and the transport keeps its own clock. Pausing isn't supported on a shared transport, because it would pause every host.

### Test against your production transport's guarantees

The harness's in-memory transport supports everything: ordered groups, deduplication, unlimited delays and any message size. Give it your production transport's capabilities so a test sees what production would do:

```csharp
builder.Services.AddFoundatioTestHarness(o => o.TransportCapabilities = new TransportCapabilities
{
    // Like an SQS standard queue: no ordered groups or deduplication, and its limits
    MaxDelay = TimeSpan.FromDays(13),
    MaxPublishDelay = TimeSpan.FromDays(13),
    MaxRetryDelay = TimeSpan.FromHours(12),
    MaxMessageSize = 256 * 1024
});
```

A group's messages then run concurrently and out of order, a repeated `DeduplicationId` is delivered again, and an oversized message throws `MessageTooLargeException`. Outside the harness, `new InMemoryMessageTransport(capabilities)` does the same.

### Catching mistakes at startup

Handler and job classes are registered in DI as scoped services (unless you register them yourself), so with `ValidateOnBuild`, which `Host.CreateApplicationBuilder()` and `WebApplication` turn on in Development, building the host fails when a handler or job needs a service that isn't registered, naming both.

Jobs are recorded too; see [testing jobs](./durable-jobs#testing).

## Health checks

With Foundatio.Extensions.Hosting, `builder.Services.AddHealthChecks().AddFoundatioMessaging()` reports each source your handlers receive from. Receive errors, and dead letters the transport refuses, report Degraded while they are retried; a processor that stopped, or one whose errors have lasted 5 minutes, reports Unhealthy. A receiver that fails five times in a row, or that its transport reports dead, is replaced at once; the failed one is closed after its in-flight messages finish (up to `ShutdownTimeout`). The host fails to start if you add the check without `AddFoundatioMessaging`.

Errors that last minutes are almost always a broker outage, and restarting the process does not fix one. Use the check for readiness, so an instance stops taking traffic until it can process again:

```csharp
builder.Services.AddHealthChecks().AddFoundatioMessaging(tags: ["ready"]);
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
```

If a liveness probe uses it, pass `failureStatus: HealthStatus.Degraded`, so an outage doesn't restart every instance. See [health checks](./durable-jobs#health-checks) for the jobs check and the states each check reports.

## Existing code using `IMessageBus`

`m.AddLegacyMessageBus()` provides the original `IMessageBus` on the configured transport, so `HybridCacheClient`, `CacheLockProvider` and your existing subscribers keep working with the same semantics: each process handles messages one at a time in arrival order, never calls a subscriber concurrently with itself, and runs a message's subscribers concurrently. It receives up to 100 messages at a time (the transport's batch limit when lower) and completes each one while the next is handled. It doesn't talk to the original buses, such as `RedisMessageBus`, so switch every process that shares a bus in the same deployment. See [Moving IMessageBus to the new transport](./migrating-to-messaging#moving-imessagebus-to-the-new-transport).

For a message type messaging knows (mapped, marked with `[Message]`, or handled by a registered handler), the legacy bus and `IMessageClient` reach each other's subscribers: an `IMessageBus.PublishAsync` reaches `AddSubscriber` handlers, and an `IMessageClient.PublishAsync` reaches `IMessageBus.SubscribeAsync` subscribers, each once. See [Mixing IMessageBus and IMessageClient](./migrating-to-messaging#mixing-imessagebus-and-imessageclient).
