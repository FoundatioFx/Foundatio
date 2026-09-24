# Messaging

Messaging allows you to publish and subscribe to messages flowing through your application using pub/sub patterns. Foundatio provides multiple message bus implementations through the `IMessageBus` interface.

## The IMessageBus Interface

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Messaging/IMessageBus.cs)

```csharp
public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable, IAsyncDisposable
{
}

public interface IMessagePublisher
{
    Task PublishAsync(Type messageType, object message,
                      MessageOptions? options = null,
                      CancellationToken cancellationToken = default);
}

public interface IMessageSubscriber
{
    Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler,
                           CancellationToken cancellationToken = default) where T : class;
}
```

## Implementations

### InMemoryMessageBus

An in-memory message bus for development and testing:

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Messaging/InMemoryMessageBus.cs)

```csharp
using Foundatio.Messaging;

var messageBus = new InMemoryMessageBus();

// Subscribe to messages
await messageBus.SubscribeAsync<OrderCreated>(async msg =>
{
    Console.WriteLine($"Order created: {msg.OrderId}");
});

// Publish a message
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

### AzureServiceBusMessageBus

Messaging using Azure Service Bus (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.AzureServiceBus/blob/main/src/Foundatio.AzureServiceBus/Messaging/AzureServiceBusMessageBus.cs)

```csharp
// dotnet add package Foundatio.AzureServiceBus

using Foundatio.AzureServiceBus.Messaging;

var messageBus = new AzureServiceBusMessageBus(o => {
    o.ConnectionString = "...";
    o.Topic = "events";
});
```

### KafkaMessageBus

Messaging using Apache Kafka (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.Kafka/blob/main/src/Foundatio.Kafka/Messaging/KafkaMessageBus.cs)

```csharp
// dotnet add package Foundatio.Kafka

using Foundatio.Kafka.Messaging;

var messageBus = new KafkaMessageBus(o => {
    o.BootstrapServers = "localhost:9092";
});
```

### RabbitMQMessageBus

Messaging using RabbitMQ (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/856d0f47b0bb979c3abb875d9c2882c586b1a230/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBus.cs)

```csharp
// dotnet add package Foundatio.RabbitMQ

using Foundatio.Messaging;

await using var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString("amqp://guest:guest@localhost:5672"));
```

The callback receives the fluent options builder. The defaults are best-effort pub/sub, including broker automatic acknowledgement and ordinary publisher confirms disabled. See the [RabbitMQ provider guide](./implementations/rabbitmq.md) and [delivery-safety guide](./implementations/rabbitmq-delivery-safety.md) for durable subscription requirements, retry/terminal outcomes, and the companion implementation's opt-in contracts. Those guides identify which behavior depends on the unreleased provider PR; a documentation branch is not a package release.

The candidate changes Automatic-mode exhaustion without a typed terminal destination from discard to retention, which can block progress and increase backlog. Strict dispatch is opt-in and requires Automatic acknowledgement, a nonempty typed `DeadLetterExchange`, and discard disabled. Both classic and quorum support these provider contracts; only quorum adds replication and optional at-least-once broker DLX. Plan [capacity and backpressure](./implementations/rabbitmq-delivery-safety.md#capacity-and-backpressure) before adoption.

### RedisMessageBus

Distributed messaging using Redis pub/sub (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.Redis/blob/main/src/Foundatio.Redis/Messaging/RedisMessageBus.cs)

```csharp
// dotnet add package Foundatio.Redis

using Foundatio.Redis.Messaging;
using StackExchange.Redis;

var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var messageBus = new RedisMessageBus(o => o.Subscriber = redis.GetSubscriber());
```

### SQSMessageBus

Messaging using AWS SNS/SQS (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.AWS/blob/master/src/Foundatio.AWS/Messaging/SQSMessageBus.cs)

```csharp
// dotnet add package Foundatio.AWS

using Foundatio.Messaging;

var messageBus = new SQSMessageBus(o => {
    o.ConnectionString = connectionString;
    o.Topic = "events";
    // Optional: Specify queue name for durable subscriptions
    // o.SubscriptionQueueName = "my-service-queue";
});
```

## Basic Usage

### Publishing Messages

```csharp
var messageBus = new InMemoryMessageBus();

// Simple publish
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });

// With options
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 }, new MessageOptions
{
    CorrelationId = "request-abc",
    DeliveryDelay = TimeSpan.FromSeconds(30),
    Properties = new Dictionary<string, string>
    {
        ["source"] = "order-service"
    }
});

// Delayed publish (extension method)
await messageBus.PublishAsync(
    new OrderReminder { OrderId = 123 },
    TimeSpan.FromHours(1)
);
```

### Subscribing to Messages

```csharp
var messageBus = new InMemoryMessageBus();

// Simple subscription
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    Console.WriteLine($"Processing order: {order.OrderId}");
});

// With cancellation token
await messageBus.SubscribeAsync<OrderCreated>(
    async (order, ct) =>
    {
        await ProcessOrderAsync(order, ct);
    },
    cancellationToken
);

// Synchronous handler
await messageBus.SubscribeAsync<OrderCreated>(order =>
{
    Console.WriteLine($"Order: {order.OrderId}");
});
```

### Multiple Subscribers

Each subscriber receives every message:

```csharp
var messageBus = new InMemoryMessageBus();

// Handler 1: Logging
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    _logger.LogInformation("Order {OrderId} created", order.OrderId);
});

// Handler 2: Notification
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    await _notificationService.SendAsync(order.CustomerId, "Order placed!");
});

// Handler 3: Analytics
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    await _analytics.TrackAsync("order_created", order.OrderId);
});

// All three handlers receive this message
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

## Message Types

### Define Your Messages

```csharp
// Simple message
public record OrderCreated
{
    public int OrderId { get; init; }
    public DateTime CreatedAt { get; init; }
    public string CustomerId { get; init; }
}

// Message with interface for grouping
public interface IOrderEvent { int OrderId { get; } }

public record OrderShipped : IOrderEvent
{
    public int OrderId { get; init; }
    public string TrackingNumber { get; init; }
}

public record OrderDelivered : IOrderEvent
{
    public int OrderId { get; init; }
    public DateTime DeliveredAt { get; init; }
}
```

### Subscribe to Interface

Subscribe to all messages implementing an interface:

```csharp
// Receives OrderShipped, OrderDelivered, and any other IOrderEvent
await messageBus.SubscribeAsync<IOrderEvent>(async orderEvent =>
{
    _logger.LogInformation("Order event: {Type} for {OrderId}",
        orderEvent.GetType().Name, orderEvent.OrderId);
});
```

### IMessage Interface

Use the built-in `IMessage` interface for raw message access:

```csharp
await messageBus.SubscribeAsync(async (IMessage message, CancellationToken ct) =>
{
    Console.WriteLine($"Type: {message.Type}");
    Console.WriteLine($"Correlation ID: {message.CorrelationId}");

    // Deserialize the data
    var order = message.GetBody<OrderCreated>();
});
```

#### Breaking change: `IMessage.Data` is now `ReadOnlyMemory<byte>`

`IMessage.Data` exposes the raw payload as `ReadOnlyMemory<byte>` instead of `byte[]`. The abstraction can represent a memory-backed payload without requiring a new array; ownership and copying depend on the provider implementation. Since it is a struct, follow these patterns:

- Check for an empty payload with `message.Data.IsEmpty` (not `== null`)
- Read the bytes directly via `message.Data.Span`
- Call `message.Data.ToArray()` only when you need a `byte[]`

**Buffer validity:** Treat `Data` as valid only for the documented handling lifetime unless the provider explicitly promises ownership. Copy it with `message.Data.ToArray()` when retaining raw bytes beyond that lifetime. The companion RabbitMQ implementation described in the [delivery-safety guide](./implementations/rabbitmq-delivery-safety.md#dispatch-and-lifecycle) makes an owned copy so an uncooperative handler cannot outlive a reclaimed transport buffer; do not generalize that implementation choice to other providers or versions.

Most code that uses `GetBody()` / `Body` is unaffected. When constructing a `Message`, you can still pass a `byte[]`; it converts implicitly to `ReadOnlyMemory<byte>`.

## Common Patterns

### Event-Driven Architecture

Decouple services with events:

```csharp
// Order Service
public class OrderService
{
    private readonly IMessageBus _messageBus;

    public async Task CreateOrderAsync(CreateOrderRequest request)
    {
        var order = await _repository.CreateAsync(request);

        // Publish event for other services
        await _messageBus.PublishAsync(new OrderCreated
        {
            OrderId = order.Id,
            CustomerId = request.CustomerId,
            CreatedAt = DateTime.UtcNow
        });
    }
}

// Inventory Service (separate process/service)
public class InventoryService
{
    public InventoryService(IMessageBus messageBus)
    {
        messageBus.SubscribeAsync<OrderCreated>(async order =>
        {
            await ReserveInventoryAsync(order.OrderId);
        });
    }
}

// Notification Service (separate process/service)
public class NotificationService
{
    public NotificationService(IMessageBus messageBus)
    {
        messageBus.SubscribeAsync<OrderCreated>(async order =>
        {
            await SendConfirmationEmailAsync(order.CustomerId);
        });
    }
}
```

These fragments illustrate event relationships, not a complete startup or transaction protocol. In a hosted application, await subscription setup during startup rather than leaving an unobserved task in a constructor. Committing application state and publishing an event are separate operations; required consistency may need an outbox and reconciliation.

### Cache Invalidation

Coordinate cache across instances:

```csharp
public class CacheInvalidationService
{
    private readonly IMessageBus _messageBus;
    private readonly ICacheClient _localCache;

    public CacheInvalidationService(IMessageBus messageBus, ICacheClient localCache)
    {
        _messageBus = messageBus;
        _localCache = localCache;

        // Listen for invalidation messages
        _messageBus.SubscribeAsync<CacheInvalidated>(async msg =>
        {
            await _localCache.RemoveAsync(msg.Key);
        });
    }

    public async Task InvalidateAsync(string key)
    {
        // Remove locally
        await _localCache.RemoveAsync(key);

        // Notify other instances
        await _messageBus.PublishAsync(new CacheInvalidated { Key = key });
    }
}

public record CacheInvalidated { public string Key { get; init; } }
```

### Real-Time Updates

Push updates to clients:

```csharp
// Server-side
public class NotificationHub
{
    private readonly IMessageBus _messageBus;

    public NotificationHub(IMessageBus messageBus)
    {
        _messageBus = messageBus;

        // Forward bus messages to SignalR/WebSocket
        _messageBus.SubscribeAsync<UserNotification>(async notification =>
        {
            await _hubContext.Clients
                .User(notification.UserId)
                .SendAsync("notification", notification);
        });
    }
}

// When something happens
await messageBus.PublishAsync(new UserNotification
{
    UserId = "user-123",
    Message = "Your order has shipped!"
});
```

### Saga/Process Manager

Coordinate multi-step processes:

```csharp
public class OrderSaga
{
    private readonly IMessageBus _messageBus;

    public OrderSaga(IMessageBus messageBus)
    {
        _messageBus = messageBus;

        // Step 1: Order created -> Reserve inventory
        _messageBus.SubscribeAsync<OrderCreated>(async order =>
        {
            await ReserveInventoryAsync(order.OrderId);
            await _messageBus.PublishAsync(new InventoryReserved { OrderId = order.OrderId });
        });

        // Step 2: Inventory reserved -> Process payment
        _messageBus.SubscribeAsync<InventoryReserved>(async evt =>
        {
            await ProcessPaymentAsync(evt.OrderId);
            await _messageBus.PublishAsync(new PaymentProcessed { OrderId = evt.OrderId });
        });

        // Step 3: Payment processed -> Ship order
        _messageBus.SubscribeAsync<PaymentProcessed>(async evt =>
        {
            await ShipOrderAsync(evt.OrderId);
        });
    }
}
```

## Message Options

Configure message delivery:

```csharp
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 }, new MessageOptions
{
    // Unique message identifier
    UniqueId = Guid.NewGuid().ToString(),

    // For tracing across services
    CorrelationId = Activity.Current?.Id,

    // Delayed delivery
    DeliveryDelay = TimeSpan.FromMinutes(5),

    // Custom properties
    Properties = new Dictionary<string, string>
    {
        ["source"] = "order-service",
        ["version"] = "1.0"
    }
});
```

## Delayed Message Delivery

The `DeliveryDelay` option schedules messages for future delivery. This is useful for scenarios like:

- **Eventual consistency** - Wait for data to propagate before processing
- **Scheduled reminders** - Send notifications after a delay
- **Retry with backoff** - Republish failed messages with increasing delays

### Basic Usage

```csharp
// Using MessageOptions
await messageBus.PublishAsync(new OrderReminder { OrderId = 123 }, new MessageOptions
{
    DeliveryDelay = TimeSpan.FromMinutes(30)
});

// Using extension method
await messageBus.PublishAsync(new OrderReminder { OrderId = 123 }, TimeSpan.FromMinutes(30));
```

### Provider Support

Different providers handle delayed delivery differently:

| Provider | Implementation | Persistence | Survives Restart |
|----------|---------------|-------------|------------------|
| **InMemoryMessageBus** | In-memory timer | None | No |
| **AzureServiceBusMessageBus** | Native `ScheduledEnqueueTime` | Azure | Yes |
| **KafkaMessageBus** | In-memory timer | None | No |
| **RabbitMQMessageBus** | Plugin or fallback | Plugin: single-node broker storage; fallback: none | Plugin can outlive publisher process; not replicated scheduling. Fallback: no. |
| **RedisMessageBus** | In-memory timer | None | No |
| **SQSMessageBus** | In-memory timer | None | No |

### Native vs Fallback Implementation

**Broker-side implementations** store scheduled work outside the publishing process. That does not establish a universal delivery guarantee: storage redundancy, retention, destination availability, and future routing must be verified for the chosen provider. In particular, the RabbitMQ delayed-exchange plugin keeps scheduled work on one broker node.

**Fallback implementations** hold the message in memory using a timer. This has important limitations:

::: warning Fallback Limitations
- **Messages are lost on restart** - If your application restarts before the delay expires, the message is permanently lost
- **Messages are discarded on disposal** - During graceful shutdown, pending delayed messages are discarded
- **Best-effort delivery** - No guarantee the message will be delivered
:::

### RabbitMQ Plugin

On the 4.2.5 baseline, Foundatio.RabbitMQ uses the `rabbitmq_delayed_message_exchange` plugin for broker-side delayed publication:

```bash
# Enable the separately installed compatible plugin artifact.
rabbitmq-plugins enable rabbitmq_delayed_message_exchange
```

By default, the provider can fall back to process-memory scheduling when the topic cannot schedule. The companion implementation's `RequireBrokerDelayedDelivery` rejects that fallback before creating a timer. It requires durable publication and confirms, but does not guarantee future routing or replicated scheduled storage. `RequirePublishRouting` validates immediate routing and rejects nonzero delays. See the [versioned RabbitMQ delivery contract](./implementations/rabbitmq-delivery-safety.md#delayed-publication).

### When to Use Delayed Delivery

**Appropriate use cases (fallback is acceptable):**

- Cache invalidation
- Non-critical notifications
- Eventual consistency delays (e.g., waiting for Elasticsearch to refresh)

**NOT appropriate for fallback:**

- Financial transactions
- Order processing
- Any message where loss is unacceptable

For required delayed work, select and verify a broker-backed scheduler or durable queue implementation against the required failure model. An `IQueue<T>` abstraction alone does not establish persistence, and the RabbitMQ plugin alone is not a replicated outbox. Use durable application publication/reconciliation where the chosen provider cannot cover the required boundary.

## Distributed Tracing

Foundatio automatically integrates with .NET's distributed tracing infrastructure (`System.Diagnostics.Activity`) to enable end-to-end request tracing across services.

### Automatic CorrelationId Injection

When you publish a message, Foundatio automatically captures the current trace context:

```csharp
// If Activity.Current exists, its ID is automatically used as CorrelationId
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });

// The message will have:
// - CorrelationId = Activity.Current?.Id
// - Properties["TraceState"] = Activity.Current?.TraceStateString (if present)
```

### Manual CorrelationId

You can also set the `CorrelationId` explicitly:

```csharp
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 }, new MessageOptions
{
    CorrelationId = "my-custom-correlation-id"
});
```

When you provide a `CorrelationId`, the automatic injection is skipped.

### Trace Propagation

When a subscriber receives a message, Foundatio:

1. Creates a new `Activity` with the message's `CorrelationId` as the parent
2. Restores the `TraceState` from message properties
3. Adds the `CorrelationId` to the logging scope

This enables distributed tracing tools (like Application Insights, Jaeger, or Zipkin) to correlate requests across services.

### Accessing Trace Information

In your subscriber, you can access the trace context:

```csharp
await messageBus.SubscribeAsync<IMessage>(async (message, ct) =>
{
    // Access correlation ID
    var correlationId = message.CorrelationId;

    // Access custom properties
    var traceState = message.Properties.GetValueOrDefault("TraceState");

    // Activity.Current is automatically set with the message's trace context
    _logger.LogInformation("Processing message with trace {TraceId}", Activity.Current?.TraceId);
});
```

### Integration with OpenTelemetry

Foundatio's tracing integrates seamlessly with OpenTelemetry:

```csharp
services.AddOpenTelemetry()
    .WithTracing(builder =>
    {
        builder.AddSource(FoundatioDiagnostics.ActivitySource.Name);
        // ... other configuration
    });
```

## Dependency Injection

### Basic Registration

```csharp
// In-memory (development)
services.AddSingleton<IMessageBus, InMemoryMessageBus>();

// Redis (production)
services.AddSingleton<IMessageBus>(sp =>
{
    var redis = sp.GetRequiredService<IConnectionMultiplexer>();
    return new RedisMessageBus(o => o.Subscriber = redis.GetSubscriber());
});
```

### Subscribe at Startup

```csharp
public class MessageSubscriber : IHostedService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceProvider _services;

    public MessageSubscriber(IMessageBus messageBus, IServiceProvider services)
    {
        _messageBus = messageBus;
        _services = services;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _messageBus.SubscribeAsync<OrderCreated>(async (msg, ct) =>
        {
            using var scope = _services.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IOrderHandler>();
            await handler.HandleAsync(msg, ct);
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Register
services.AddHostedService<MessageSubscriber>();
```

## Error Handling

### MessageBusException

`MessageBusException` represents message-bus errors. Consult the specific provider/version for exceptions that can also surface during construction, subscription setup, and broker operations:

```csharp
try
{
    await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
}
catch (MessageBusException ex)
{
    _logger.LogError(ex, "Failed to publish message: {Message}", ex.Message);
    // Handle transport error (network, broker unavailable, etc.)
}
catch (OperationCanceledException)
{
    // Handle cancellation
}
```

**Exception Behavior:**

| Scenario | Exception Type | Notes |
|----------|---------------|-------|
| Transport error (network, broker) | `MessageBusException` or provider-specific exception | Verify the operation and provider contract; setup errors need not have the same wrapper as publish errors. |
| Null message/type | `ArgumentNullException` | Thrown immediately |
| Cancellation requested | `OperationCanceledException` | Passed through unchanged |
| Serialization error | `MessageBusException` | Wraps serialization exception |

### Subscriber Error Handling

Subscriber failure is not an acknowledgement sent back to the producer. For RabbitMQ, a broker-confirmed publication does not report the outcome of a downstream handler. Handler exceptions and acknowledgement mode determine the consumer-side retry/terminal outcome; inspect the provider-specific contract rather than assuming identical guarantees from the shared interface.

```csharp
// Publisher - a downstream handler failure is not a consumer-processing confirmation.
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });

// Subscriber - allow failures to propagate when the transport must retry.
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    await ProcessOrderAsync(order);
});
```

Catching an error and returning successfully can cause an acknowledgement-based provider to treat the delivery as handled. Only do so when the application's selected outcome is actually complete, not merely because the error was logged.

### Provider Behavior Summary

| Provider | Publish Errors | Subscriber Errors | Redelivery on Failure |
|----------|---------------|-------------------|----------------------|
| **InMemoryMessageBus** | Throws `MessageBusException` | Logged, swallowed | No |
| **AzureServiceBusMessageBus** | Throws `MessageBusException` | Logged, SDK handles | Yes (`MaxDeliveryCount`) |
| **KafkaMessageBus** | Fire-and-forget with callback | Logged, offset not committed | Yes (redelivered) |
| **RabbitMQMessageBus** | Broker confirmation is separate from handler completion | Depends on acknowledgement mode and the versioned retry/terminal policy | Requires Automatic acknowledgements and suitable retained topology; default FireAndForget does not provide processing-failure redelivery |
| **RedisMessageBus** | Throws `MessageBusException` | Logged, swallowed | No (pub/sub has no ack) |
| **SQSMessageBus** | Throws `MessageBusException` | Logged, message not deleted | Yes (redelivered) |

::: tip Logging
Log levels and the number of emitted records depend on the provider and failure path. A log entry is not evidence of acknowledgement, retry completion, or durable quarantine. For RabbitMQ, combine the exposed subscription/delivery status with broker state and actual application progress.
:::

### In Subscribers

```csharp
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    try
    {
        await ProcessOrderAsync(order);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to process order {OrderId}", order.OrderId);

        // Optionally publish failure event
        await _messageBus.PublishAsync(new OrderProcessingFailed
        {
            OrderId = order.OrderId,
            Error = ex.Message
        });
    }
});
```

### With Retry

```csharp
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    await _resiliencePolicy.ExecuteAsync(async ct =>
    {
        await ProcessOrderAsync(order, ct);
    });
});
```

## Cancellation Token Behavior

Understanding how cancellation tokens are handled internally is important for building reliable publishers and subscribers.

### Resource Creation Uses Disposal Token

Message-bus setup hooks use the internal disposal token so one caller's cancellation does not cancel infrastructure shared by other callers. This is not a guarantee that setup must succeed: provider setup timeouts, broker errors, and disposal can still abort it. The companion RabbitMQ provider cleans up partial initialization and rolls back the failed local registration; see its [lifecycle contract](./implementations/rabbitmq-delivery-safety.md#dispatch-and-lifecycle).

### Linked Cancellation for Publish

The actual publish operation observes caller/disposal cancellation. Cancellation does not undo an already accepted broker publication, and its outcome can be ambiguous. A caller timeout is not automatically a total deadline covering shared setup, recovery, and every cleanup step.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 }, cancellationToken: cts.Token);
```

### For Implementation Authors

If you are writing a custom `IMessageBus` implementation by extending `MessageBusBase<TOptions>`:

- **`EnsureTopicCreatedAsync`** always receives `DisposedCancellationToken`. Use it for all setup operations (lock acquisition, API calls, etc.).
- **`EnsureTopicSubscriptionAsync`** always receives `DisposedCancellationToken`. Use it for subscription infrastructure setup.
- **`PublishImplAsync`** receives a linked token (caller + disposal). Respect it for the actual message send.

## Best Practices

### 1. Use Immutable Messages

```csharp
// ✅ Good: Immutable record
public record OrderCreated
{
    public int OrderId { get; init; }
    public required string CustomerId { get; init; }
}

// ❌ Bad: Mutable class
public class OrderCreated
{
    public int OrderId { get; set; }
    public string CustomerId { get; set; }
}
```

### 2. Include Timestamp and Correlation

```csharp
public record OrderCreated
{
    public int OrderId { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public string CorrelationId { get; init; } = Activity.Current?.Id;
}
```

### 3. Handle Idempotency

```csharp
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    // Check if already processed
    if (await _processedEvents.ContainsAsync(order.EventId))
    {
        _logger.LogDebug("Already processed {EventId}", order.EventId);
        return;
    }

    await ProcessOrderAsync(order);
    await _processedEvents.AddAsync(order.EventId);
});
```

This check/process/record example illustrates the intent, not an atomic idempotency implementation. Concurrent attempts and a crash between the side effect and the record can repeat the effect. Required processing needs durable consumer-scoped identity and a transaction or application-specific idempotent operation that covers the protected side effect.

### 4. Use Specific Message Types

```csharp
// ✅ Good: Specific, intentional messages
public record OrderCreated { ... }
public record OrderShipped { ... }
public record OrderCancelled { ... }

// ❌ Bad: Generic, multi-purpose messages
public record OrderEvent { public string Action { get; set; } }
```

### 5. Keep Messages Small

Messages should contain identifiers and essential data only, not full entity payloads.

```csharp
// ✅ Good: Just identifiers
public record OrderCreated
{
    public int OrderId { get; init; }
}

// ❌ Bad: Full entity in message
public record OrderCreated
{
    public Order FullOrderWithAllDetails { get; init; }
}
```

## Message Size Limits

Different message bus implementations have different size limits. Understanding these limits is essential for reliable messaging.

| Provider | Max Message Size | Notes |
|----------|------------------|-------|
| InMemoryMessageBus | Limited by available memory | No practical limit |
| AzureServiceBusMessageBus | 256 KB (Standard) / 100 MB (Premium) | Use claim check for large payloads |
| KafkaMessageBus | 1 MB (default) | Configurable via `message.max.bytes` |
| RabbitMQMessageBus | Broker/client configuration | Verify the deployed broker's `max_message_size` and client limits; do not assume a fixed provider-wide 128 MB default. |
| RedisMessageBus | 512 MB (Redis limit) | Recommended: < 1 MB for performance |
| SQSMessageBus | 256 KB | Use claim check for large payloads |

### Claim Check Pattern for Large Payloads

For large data, store it externally and pass a reference (also known as the Claim Check Pattern):

```csharp
// Instead of embedding large data
public record DocumentProcessed
{
    public string DocumentId { get; init; }
    public string BlobPath { get; init; }  // Reference to storage
    public long SizeBytes { get; init; }
}

// Subscriber retrieves from storage
await messageBus.SubscribeAsync<DocumentProcessed>(async msg =>
{
    var document = await _fileStorage.GetObjectAsync<Document>(msg.BlobPath);
    await ProcessDocumentAsync(document);
});
```

## Notification Patterns

### Real-Time Notifications with SignalR

```csharp
public class NotificationService : IHostedService
{
    private readonly IMessageBus _messageBus;
    private readonly IHubContext<NotificationHub> _hubContext;

    public NotificationService(IMessageBus messageBus, IHubContext<NotificationHub> hubContext)
    {
        _messageBus = messageBus;
        _hubContext = hubContext;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Bridge message bus to SignalR
        await _messageBus.SubscribeAsync<UserNotification>(async (msg, ct) =>
        {
            await _hubContext.Clients
                .User(msg.UserId)
                .SendAsync("Notification", msg.Title, msg.Body, ct);
        }, ct);

        // Broadcast to all users
        await _messageBus.SubscribeAsync<SystemAnnouncement>(async (msg, ct) =>
        {
            await _hubContext.Clients.All
                .SendAsync("Announcement", msg.Message, ct);
        }, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

### Delayed Notifications

```csharp
// Schedule a reminder
await messageBus.PublishAsync(new ReminderNotification
{
    UserId = "user-123",
    Message = "Don't forget to complete your order!"
}, new MessageOptions
{
    DeliveryDelay = TimeSpan.FromHours(24)
});
```

### Fan-Out Pattern

Establish the subscriptions before publishing a message that they must receive:

```csharp
// Multiple subscribers handle different concerns
await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    await _emailService.SendConfirmationAsync(order.OrderId);
});

await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    await _inventoryService.ReserveAsync(order.OrderId);
});

await messageBus.SubscribeAsync<OrderCreated>(async order =>
{
    await _analyticsService.TrackAsync("order_created", order.OrderId);
});

// Single publish after the subscriptions are established
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

For RabbitMQ, independent durable subscription queues receive separate fanout copies; replicas consuming the same queue compete for a delivery. Multiple local handlers on one bus share that delivery's retry boundary.

## Resource Management

### Disposal Lifecycle

Message buses implement both `IDisposable` and `IAsyncDisposable`. Prefer `await using` (or `DisposeAsync()`) for clean shutdown:

```csharp
// Preferred: async disposal
await using var messageBus = new InMemoryMessageBus();
await messageBus.SubscribeAsync<MyEvent>(async e => { /* ... */ });
// DisposeAsync is called when scope ends

// DI container manages lifetime automatically
services.AddSingleton<IMessageBus, InMemoryMessageBus>();
```

Disposal provides a **two-phase** provider lifecycle; it is not by itself a no-loss guarantee:

1. **Shutdown hook** — `ShutdownAsync` runs before the base disposal token is cancelled and local subscribers are cleared. The provider may drain, cancel, or stop transport processing here. The companion RabbitMQ implementation signals handler cancellation and invalidates delivery generations; it does not promise every arbitrary handler finishes.
2. **Teardown** — The base cancellation token is cancelled, subscribers are cleared, and `CleanupAsync` tears down the remaining provider resources.

> **Note:** The base `MessageBusBase` implementation does not guarantee that all active subscriber callbacks have completed before `DisposeAsync` returns. Provider-specific draining behavior (such as Azure Service Bus `StopProcessingAsync`) is implemented in provider overrides of `ShutdownAsync`.

### Message Durability During Shutdown

What happens to messages that arrive while the bus is disposing depends on the provider:

| Provider | In-Flight Messages | Arriving During Dispose | After Dispose |
|---|---|---|---|
| **InMemoryMessageBus** | Completed normally | Dropped (no persistence) | Lost |
| **AzureServiceBusMessageBus** | Completed; abandoned if bus disposes mid-handler (PeekLock) | Remain in topic for other subscribers | Persisted in Azure |
| **KafkaMessageBus** | Completed; offset not committed if bus disposes mid-handler | Remain in partition (uncommitted offset) | Persisted in Kafka |
| **RabbitMQMessageBus** | With `AcknowledgementStrategy.Automatic`, unacknowledged work can return to a retained queue; `FireAndForget` cannot recover already auto-acked deliveries | Depends on routing, queue lifetime, and retention policy | Requires retained durable topology and suitable broker policy, not the provider defaults alone |
| **RedisMessageBus** | Completed normally | Dropped (pub/sub has no persistence) | Lost |
| **SQSMessageBus** | Completed; message not deleted if bus disposes mid-handler | Remain in SQS queue | Persisted in SQS |

::: tip Fire-and-Forget Providers
`InMemoryMessageBus` and `RedisMessageBus` use fire-and-forget delivery. RabbitMQ also defaults to broker automatic acknowledgement. Select the actual acknowledgement, topology, and retention contract needed for required work; a provider label alone is not a guarantee.
:::

### Writing Custom Providers

If you are extending `MessageBusBase<TOptions>` with a custom provider, override the lifecycle hooks:

- **`ShutdownAsync()`** — Called *before* the base cancellation token is cancelled and *before* subscribers are cleared. Implement the provider's explicit stop/drain/cancellation policy here; do not assume all implementations drain callbacks.
- **`CleanupAsync()`** — Called *after* the cancellation token is cancelled and *after* subscribers are cleared. Use this to tear down transport infrastructure (close connections, dispose clients, await background tasks).

```csharp
public class MyMessageBus : MessageBusBase<MyOptions>
{
    // Phase 1: Stop accepting new messages, drain in-flight work.
    // When the body is a single call, return the Task directly (no extra async state machine).
    protected override Task ShutdownAsync() => _processor.StopAsync();

    protected override async Task CleanupAsync()
    {
        // Phase 2: Close connections, dispose clients — ConfigureAwait(false) in library overrides
        await _connection.CloseAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
```

If `ShutdownAsync` needs multiple steps, use `async`/`await` and apply `.ConfigureAwait(false)` to each await (within Foundatio provider projects, the same pattern uses the internal `AnyContext()` helper).

## Next Steps

- [Queues](./queues) - Queue processing and acknowledgement contracts
- [Caching](./caching) - Cache invalidation with messaging
- [Jobs](./jobs) - Background processing triggered by messages
