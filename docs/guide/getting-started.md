# Getting Started

This guide will walk you through installing Foundatio and using your first abstractions.

## Installation

Foundatio is available on [NuGet](https://www.nuget.org/packages?q=Foundatio). Install the core package:

```bash
dotnet add package Foundatio
```

For specific implementations, install the corresponding packages:

```bash
# Redis implementations
dotnet add package Foundatio.Redis

# Azure Storage (Queues, Blobs)
dotnet add package Foundatio.AzureStorage

# Azure Service Bus (Queues, Messaging)
dotnet add package Foundatio.AzureServiceBus

# AWS (SQS, S3)
dotnet add package Foundatio.AWS

# RabbitMQ (Messaging)
dotnet add package Foundatio.RabbitMQ

# Kafka (Messaging)
dotnet add package Foundatio.Kafka

# Aliyun OSS (Storage)
dotnet add package Foundatio.Aliyun

# MinIO (S3-compatible Storage)
dotnet add package Foundatio.Minio

# SSH/SFTP (Storage)
dotnet add package Foundatio.Storage.SshNet
```

## Basic Setup

### 1. Register Services

Configure Foundatio services in your application's dependency injection container:

```csharp
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Lock;
using Foundatio.Storage;
using Foundatio.Queues;

var builder = WebApplication.CreateBuilder(args);

// Register core services
builder.Services.AddSingleton<ICacheClient, InMemoryCacheClient>();
builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
builder.Services.AddSingleton<IFileStorage, InMemoryFileStorage>();

// Register lock provider (depends on cache and message bus)
builder.Services.AddSingleton<ILockProvider>(sp =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetRequiredService<IMessageBus>()
    )
);

// Register queues
builder.Services.AddSingleton<IQueue<WorkItem>>(sp =>
    new InMemoryQueue<WorkItem>()
);

var app = builder.Build();
```

### 2. Use the Services

Inject and use the services in your application:

```csharp
public class OrderService
{
    private readonly ICacheClient _cache;
    private readonly IQueue<OrderWorkItem> _queue;
    private readonly ILockProvider _locker;
    private readonly IMessageBus _messageBus;

    public OrderService(
        ICacheClient cache,
        IQueue<OrderWorkItem> queue,
        ILockProvider locker,
        IMessageBus messageBus)
    {
        _cache = cache;
        _queue = queue;
        _locker = locker;
        _messageBus = messageBus;
    }

    public async Task<Order> CreateOrderAsync(CreateOrderRequest request)
    {
        // Use distributed lock to prevent duplicate orders
        await using var lck = await _locker.AcquireAsync($"order:{request.CustomerId}");
        if (lck == null)
            throw new InvalidOperationException("Could not acquire lock");

        // Create order
        var order = new Order { Id = Guid.NewGuid(), CustomerId = request.CustomerId };

        // Cache the order
        await _cache.SetAsync($"order:{order.Id}", order, TimeSpan.FromHours(1));

        // Queue for background processing
        await _queue.EnqueueAsync(new OrderWorkItem { OrderId = order.Id });

        // Publish event for other services
        await _messageBus.PublishAsync(new OrderCreatedEvent { OrderId = order.Id });

        return order;
    }
}
```

## Switching to Production Implementations

When moving to production, swap in-memory implementations for distributed ones:

```csharp
using Foundatio.Redis.Cache;
using Foundatio.Redis.Messaging;
using Foundatio.Redis.Queues;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Configure Redis connection
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

// Use Redis implementations
builder.Services.AddSingleton<ICacheClient>(sp =>
    new RedisCacheClient(o => o.ConnectionMultiplexer = redis)
);

builder.Services.AddSingleton<IMessageBus>(sp =>
    new RedisMessageBus(o => o.Subscriber = redis.GetSubscriber())
);

builder.Services.AddSingleton<IQueue<WorkItem>>(sp =>
    new RedisQueue<WorkItem>(o => o.ConnectionMultiplexer = redis)
);
```

Your application code remains unchanged - only the DI registration changes!

## Working with Extension Methods

Foundatio provides convenient extension methods through `FoundatioServicesExtensions`:

```csharp
using Foundatio;

var builder = WebApplication.CreateBuilder(args);

// Add Foundatio with default in-memory implementations
builder.Services.AddFoundatio();

// Or configure with options
builder.Services.AddFoundatio(options =>
{
    options.UseInMemoryCache();
    options.UseInMemoryMessageBus();
    options.UseInMemoryQueues();
    options.UseInMemoryStorage();
});
```

## Sample Application

Here's a complete example showing all major abstractions working together:

```csharp
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Storage;

// Setup services
var cache = new InMemoryCacheClient();
var messageBus = new InMemoryMessageBus();
var storage = new InMemoryFileStorage();
var locker = new CacheLockProvider(cache, messageBus);
var queue = new InMemoryQueue<WorkItem>();

// Subscribe to messages
await messageBus.SubscribeAsync<WorkCompleted>(msg =>
{
    Console.WriteLine($"Work completed: {msg.ItemId}");
});

// Store a file
await storage.SaveFileAsync("config.json", """{"setting": "value"}""");

// Queue work
await queue.EnqueueAsync(new WorkItem { Id = "item-1" });

// Process queue with locking
while (true)
{
    var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
    if (entry == null) break;

    // Acquire lock for this item
    await using var lck = await locker.AcquireAsync($"work:{entry.Value.Id}");
    if (lck != null)
    {
        // Cache progress
        await cache.SetAsync($"progress:{entry.Value.Id}", "processing");

        // Do work...

        // Complete entry
        await entry.CompleteAsync();

        // Publish completion event
        await messageBus.PublishAsync(new WorkCompleted { ItemId = entry.Value.Id });
    }
    else
    {
        // Couldn't get lock, abandon for retry
        await entry.AbandonAsync();
    }
}

public record WorkItem { public string Id { get; init; } }
public record WorkCompleted { public string ItemId { get; init; } }
```

## Next Steps

Now that you have the basics working, explore more advanced features:

- [Caching](./caching) - Deep dive into caching patterns
- [Queues](./queues) - Queue processing and behaviors
- [Locks](./locks) - Distributed locking strategies
- [Messaging](./messaging) - Pub/sub patterns
- [Storage](./storage) - File storage operations
- [Jobs](./jobs) - Background job processing
- [Resilience](./resilience) - Retry policies and circuit breakers

## LLM-Friendly Documentation

For AI assistants and Large Language Models, we provide optimized documentation formats:

- [📜 LLMs Index](/llms.txt) - Quick reference with links to all sections
- [📖 Complete Documentation](/llms-full.txt) - All docs in one LLM-friendly file

These files follow the [llmstxt.org](https://llmstxt.org/) standard and contain the same information as this documentation in a format optimized for AI consumption.
