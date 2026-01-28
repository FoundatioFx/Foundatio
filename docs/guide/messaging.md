# Messaging

Messaging allows you to publish and subscribe to messages flowing through your application using pub/sub patterns. Foundatio provides multiple message bus implementations through the `IMessageBus` interface.

## The IMessageBus Interface

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Messaging/IMessageBus.cs)

```csharp
public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable
{
}

public interface IMessagePublisher
{
    Task PublishAsync(Type messageType, object message,
                      MessageOptions options = null,
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

### RabbitMQMessageBus

Messaging using RabbitMQ (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/main/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBus.cs)

```csharp
// dotnet add package Foundatio.RabbitMQ

using Foundatio.RabbitMQ.Messaging;

var messageBus = new RabbitMQMessageBus(o => {
    o.ConnectionString = "amqp://guest:guest@localhost:5672";
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
| **RedisMessageBus** | In-memory timer | None | No |
| **RabbitMQMessageBus** | Plugin or fallback | Plugin: Yes, Fallback: No | Plugin: Yes, Fallback: No |
| **KafkaMessageBus** | In-memory timer | None | No |
| **AzureServiceBusMessageBus** | Native `ScheduledEnqueueTime` | Azure | Yes |

### Native vs Fallback Implementation

**Native implementations** (Azure Service Bus, RabbitMQ with plugin) persist the delayed message in the broker. The message survives application restarts and is delivered reliably.

**Fallback implementations** hold the message in memory using a timer. This has important limitations:

::: warning Fallback Limitations
- **Messages are lost on restart** - If your application restarts before the delay expires, the message is permanently lost
- **Messages are discarded on disposal** - During graceful shutdown, pending delayed messages are discarded
- **Best-effort delivery** - No guarantee the message will be delivered
:::

### RabbitMQ Plugin

RabbitMQ requires the `rabbitmq_delayed_message_exchange` plugin for native delayed delivery:

```bash
# Enable the plugin
rabbitmq-plugins enable rabbitmq_delayed_message_exchange
```

The `RabbitMQMessageBus` automatically detects if the plugin is available and uses it when present. Otherwise, it falls back to the in-memory timer.

### When to Use Delayed Delivery

**Appropriate use cases (fallback is acceptable):**

- Cache invalidation
- Non-critical notifications
- Eventual consistency delays (e.g., waiting for Elasticsearch to refresh)

**NOT appropriate for fallback:**

- Financial transactions
- Order processing
- Any message where loss is unacceptable

For guaranteed delayed delivery, use:
- Azure Service Bus (native support)
- RabbitMQ with the delayed message plugin
- `IQueue<T>` with `DeliveryDelay` for work items that must be processed

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
| RedisMessageBus | 512 MB (Redis limit) | Recommended: < 1 MB for performance |
| RabbitMQMessageBus | 128 MB (default) | Configurable, but keep small |
| KafkaMessageBus | 1 MB (default) | Configurable via `message.max.bytes` |
| AzureServiceBusMessageBus | 256 KB (Standard) / 100 MB (Premium) | Use claim check for large payloads |

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

Publish once, process in multiple ways:

```csharp
// Single publish
await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });

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
```

## Resource Management

### Proper Disposal

Message buses implement `IDisposable` and should be properly disposed:

```csharp
// ✅ Good: DI container manages lifetime
services.AddSingleton<IMessageBus, InMemoryMessageBus>();

// ✅ Good: Manual disposal when needed
await using var messageBus = new InMemoryMessageBus();
await messageBus.SubscribeAsync<MyEvent>(async e => { });
// Disposed when scope ends

// ❌ Bad: Not disposing
var messageBus = new InMemoryMessageBus();
// ... use it
// Never disposed, subscriptions leak
```

## Next Steps

- [Queues](./queues) - For guaranteed delivery with acknowledgment
- [Caching](./caching) - Cache invalidation with messaging
- [Jobs](./jobs) - Background processing triggered by messages
