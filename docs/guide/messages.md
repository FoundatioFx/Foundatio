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
    .UseInMemory()                        // or a provider, e.g. UseRedis(...)
    .AddSubscriber<OrderPlacedHandler>()  // events
    .AddConsumer<SendReceiptHandler>());  // commands

// Anywhere
app.MapPost("/orders", (IMessageClient messages) => messages.PublishAsync(new OrderPlaced(1, "a@b.com")));
```

Registered handlers start receiving when the host starts. A process that registers no handlers only sends.

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

Each service's subscription is named after the host's application name. Set it explicitly with `m.UseServiceName("billing")` so a rename never orphans a subscription.

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

## Dead letters

Every queue and durable subscription has a dead-letter queue. Inspect and replay it with `IMessageAdmin`, on any transport:

```csharp
var source = MessageSource.Subscription("orders.order-placed", "billing");   // or MessageSource.Queue("orders.send-receipt")

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
    DeduplicationId = "receipt-1",      // dropped if seen recently, where supported
    CorrelationId = "order-1",          // defaults to the message or job being handled
});
```

Delays longer than the transport supports natively (for example 15 minutes on SQS) are handled automatically for sends. A publish delay beyond the transport's `MaxPublishDelay` throws, because re-sending it would deliver duplicates to every subscriber; send a command or schedule a job instead. A message larger than the transport allows throws `MessageTooLargeException` before anything is sent. `SendBatchAsync` and `PublishBatchAsync` send many messages in as few broker calls as possible.

With `GroupKey` on a transport that orders groups, messages of one group are handled one at a time, in order. When one is retried, the group's later messages wait for it, and each of them counts that as a delivery attempt.

## Message names and routing

A message's name identifies it on the wire. By default it is the type's full name; set a stable one with `[Message("orders.order-placed")]` or `m.MapMessage<OrderPlaced>("orders.order-placed")`. By default a command goes to a queue, and an event to a topic, named after the message. Several types can share a topic:

```csharp
[Message("orders.shipped", Topic = "orders")] public record OrderShipped(int OrderId);
[Message("orders.cancelled", Topic = "orders")] public record OrderCancelled(int OrderId);
```

A subscriber ignores types on its topic that it has no handler for. A handler for a base type receives mapped derived types.

## Testing

Unit test a handler with `TestMessageContext`:

```csharp
var context = new TestMessageContext();
await new SendReceiptHandler(fakeEmail).HandleAsync(new SendReceipt(1, "a@b.com"), context, CancellationToken.None);
Assert.Null(context.DeadLetterReason);
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

- `SentMessages`, `PublishedMessages` and `HandledMessages` record each message with its id, correlation id, attempt and headers. Deliveries also record how they ended (`Completed`, `Retried`, `DeadLettered`, `Skipped`, `Expired` or `Released`) and the error. `Sent<T>()` and `Published<T>()` return the messages themselves.
- `WaitForHandledAsync<T>(count)` and `WaitForDeadLetteredAsync<T>(count)` return the deliveries once there are enough. On timeout (30 seconds by default) the exception says what happened instead.
- `WaitForIdleAsync()` waits until no message is waiting for or being handled by your handlers, and no job is queued or running. Messages sent to queues your app does not consume, such as another service's commands, are ignored.
- Waits use real time, and retries wait for their policy's delay, so give tests a short one: `m.Configure(o => o.DefaultRetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(10)) })`.

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
