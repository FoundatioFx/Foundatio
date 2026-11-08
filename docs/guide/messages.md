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

## Options when sending

```csharp
await messages.SendAsync(new SendReceipt(1, "a@b.com"), new SendOptions
{
    Delay = TimeSpan.FromMinutes(10),   // or DeliverAt
    TimeToLive = TimeSpan.FromHours(1), // discard if not handled by then
    GroupKey = "customer-42",           // ordered delivery where the transport supports it
    DeduplicationId = "receipt-1",      // dropped if seen recently, where supported
    CorrelationId = "order-1",          // defaults to the message or job being handled
});
```

Delays longer than the transport supports natively (for example 15 minutes on SQS) are handled automatically for sends. `SendBatchAsync` and `PublishBatchAsync` send many messages in as few broker calls as possible.

## Message names and routing

A message's name identifies it on the wire. By default it is the type's full name; set a stable one with `[Message("orders.order-placed")]` or `m.MapMessage<OrderPlaced>("orders.order-placed")`. By default a command goes to a queue, and an event to a topic, named after the message. Several types can share a topic:

```csharp
[Message("orders.shipped", Topic = "orders")] public record OrderShipped(int OrderId);
[Message("orders.cancelled", Topic = "orders")] public record OrderCancelled(int OrderId);
```

A subscriber ignores types on its topic that it has no handler for. A handler for a base type receives mapped derived types.

## Testing handlers

```csharp
var context = new TestMessageContext();
await new SendReceiptHandler(fakeEmail).HandleAsync(new SendReceipt(1, "a@b.com"), context, CancellationToken.None);
Assert.Null(context.DeadLetterReason);
```

For integration tests, use `UseInMemory()`; `InMemoryMessageTransport.WaitUntilIdleAsync()` waits until every message has been handled.

## Existing code using `IMessageBus`

`m.AddLegacyMessageBus()` provides the original `IMessageBus` on the configured transport, so `HybridCacheClient`, `CacheLockProvider` and your existing subscribers keep working with the same semantics.
