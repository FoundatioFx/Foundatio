# Messages and Events

Send commands to one consumer, or publish events to every interested service. Handlers are plain classes resolved from DI; Foundatio takes care of serialization, retries, dead letters, concurrency, lease renewal and graceful shutdown.

> This replaces `IQueue<T>` and `IMessageBus` for new code. Both remain available; see [Migrating](./migrating-to-messaging).

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
    .UseInMemory()                        // or UseRedis(), UseSqs(), UseKafka(...), UseAzureServiceBus(...) or UseRabbitMQ(...); see Transports
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

- Without a connection string (`UseSqs()`), the default AWS credential and region resolution is used, such as an IAM role.
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
- See the [RabbitMQ README](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio.RabbitMQ#message-transport-preview) for how it maps to RabbitMQ, headers, counts and limits.

## Send or publish?

| | `SendAsync` (command) | `PublishAsync` (event) |
|---|---|---|
| Who gets it | One consumer | Every subscribing service |
| Register with | `AddConsumer<THandler>()` | `AddSubscriber<THandler>()` |
| Replicas of one service | Compete | Compete for the service's copy |
| While no one is listening | Waits in the queue | Copies wait in each existing subscription; with no subscriptions it is dropped |

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
- They run with one `RetryPolicy`, `LeaseDuration` and `MaxConcurrency`. The host fails to start when handlers that share a subscription set different values. A value set on one handler and left unset on the other counts as different. Two `RetryPolicy` objects built separately differ when their delegates do, so share one instance.

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

Delivery is at least once: a crash or lost lease delivers a message again, so make handlers idempotent.

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

Every queue and durable subscription has a dead-letter queue. Count, inspect and replay it with `IMessageAdmin`, on any transport:

```csharp
var source = MessageSource.Subscription("orders.order-placed", "billing");   // or MessageSource.Queue("orders.send-receipt")

MessageSourceStats? counts = await admin.GetStatsAsync(source);   // Queued, Delayed, InFlight, DeadLettered; null where the transport can't tell
IReadOnlyList<DeadLetterMessage> failed = await admin.PeekDeadLettersAsync(source);   // Reason, MessageType, Message (deserialized), Headers
int replayed = await admin.ReplayDeadLettersAsync(source);   // back to that queue or subscription only
await admin.PurgeDeadLettersAsync(source);
await admin.DeleteAsync(MessageSource.Subscription("orders.order-placed", "retired-service"));   // stop collecting copies for a removed service
```

Replayed messages keep their id, body and headers and are handled as new deliveries; the dead-letter reason, delay and expiry are dropped.

## Options when sending

```csharp
await messages.SendAsync(new SendReceipt(1, "a@b.com"), new SendOptions
{
    Delay = TimeSpan.FromMinutes(10),   // or DeliverAt
    TimeToLive = TimeSpan.FromHours(1), // discard if not handled by then
    GroupKey = "customer-42",           // ordered delivery where the transport supports it (ids and keys: up to 128 characters)
    DeduplicationId = "receipt-1",      // dropped if seen recently, where the transport supports it
    CorrelationId = "order-1",          // defaults to the message or job being handled
});
```

Delays longer than the transport supports natively (for example 15 minutes on SQS) are handled automatically for sends. A publish delay beyond the transport's `MaxPublishDelay` throws, because re-sending it would deliver duplicates to every subscriber; send a command or schedule a job instead. A message larger than the transport allows throws `MessageTooLargeException` before anything is sent. `SendBatchAsync` and `PublishBatchAsync` send many messages in as few broker calls as possible.

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

A subscriber ignores types on its topic that it has no handler for. A handler for a base type or interface receives derived types, including ones published by another service that this one doesn't know; it receives them deserialized as the type it handles.

## Testing

Unit test a handler with `TestMessageContext`. It records the outcome the handler chose and the progress it reported:

```csharp
var context = new TestMessageContext();
await new SendReceiptHandler(fakeEmail).HandleAsync(new SendReceipt(1, "a@b.com"), context, CancellationToken.None);
Assert.Null(context.DeadLetterReason);
Assert.Empty(context.ProgressReports);   // every report, in order; Progress and ProgressMessage hold the last values
```

To test your real registrations end to end, add `AddFoundatioTestHarness()` (namespace `Foundatio.Testing`) after them. It swaps your transport and job store for in-memory ones and records what happens, so a test acts, waits for the outcome and asserts, with no sleeps:

```csharp
var builder = Host.CreateApplicationBuilder();
builder.Services.AddOrdersApp();               // your AddFoundatioMessaging and AddFoundatioJobs calls
builder.Services.AddFoundatioTestHarness();    // last, so it replaces the transport and store
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
Assert.Equal(3, deadLetter.Attempt);
```

- `SentMessages`, `PublishedMessages` and `HandledMessages` record each message with its id, correlation id, attempt and headers. Deliveries also record how they ended (`Completed`, `Retried`, `DeadLettered`, `Skipped`, `Expired` or `Released`), the error, and the last `Progress` and `ProgressMessage` the handler reported. `Sent<T>()` and `Published<T>()` return the messages themselves.
- `WaitForHandledAsync<T>(count)` and `WaitForDeadLetteredAsync<T>(count)` return the deliveries once there are enough. On timeout (30 seconds by default) the exception says what happened instead.
- `WaitForIdleAsync()` waits until no message is waiting for or being handled by your handlers, and no job is queued or running. Messages sent to queues your app does not consume, such as another service's commands, are ignored.
- Waits use real time, and retries wait for their policy's delay, so give tests a short one: `m.Configure(o => o.DefaultRetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(10)) })`.

### Test against your production transport's guarantees

The harness's in-memory transport supports everything: ordered groups, deduplication, unlimited delays and any message size. Give it your production transport's capabilities so a test sees what production would do:

```csharp
builder.Services.AddFoundatioTestHarness(o => o.TransportCapabilities = new TransportCapabilities
{
    // Like an SQS standard queue: no ordered groups or deduplication, and its limits
    MaxDelay = TimeSpan.FromMinutes(15),
    MaxPublishDelay = TimeSpan.FromDays(13),
    MaxRetryDelay = TimeSpan.FromHours(12),
    MaxMessageSize = 256 * 1024
});
```

A group's messages then run concurrently and out of order, a repeated `DeduplicationId` is delivered again, and an oversized message throws `MessageTooLargeException`. Outside the harness, `new InMemoryMessageTransport(capabilities)` does the same.

Jobs are recorded too; see [testing jobs](./durable-jobs#testing).

## Health checks

With Foundatio.Extensions.Hosting, `builder.Services.AddHealthChecks().AddFoundatioMessaging()` reports each source your handlers receive from. Receive errors, and dead letters the transport refuses, report Degraded while they are retried; a processor that stopped, or one whose errors have lasted 5 minutes, reports Unhealthy. A receiver that fails five times in a row, or that its transport reports dead, is replaced after its in-flight messages finish. The host fails to start if you add the check without `AddFoundatioMessaging`.

Errors that last minutes are almost always a broker outage, and restarting the process does not fix one. Use the check for readiness, so an instance stops taking traffic until it can process again:

```csharp
builder.Services.AddHealthChecks().AddFoundatioMessaging(tags: ["ready"]);
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
```

If a liveness probe uses it, pass `failureStatus: HealthStatus.Degraded`, so an outage doesn't restart every instance. See [health checks](./durable-jobs#health-checks) for the jobs check and the states each check reports.

## Existing code using `IMessageBus`

`m.AddLegacyMessageBus()` provides the original `IMessageBus` on the configured transport, so `HybridCacheClient`, `CacheLockProvider` and your existing subscribers keep working with the same semantics.
