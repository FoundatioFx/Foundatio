# Redis Implementation

Foundatio provides Redis implementations for caching, queues, messaging, locks, and file storage. Redis enables distributed scenarios across multiple processes and servers.

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `RedisCacheClient` | `ICacheClient` | Foundatio.Redis |
| `RedisHybridCacheClient` | `ICacheClient` | Foundatio.Redis |
| `RedisQueue<T>` | `IQueue<T>` | Foundatio.Redis |
| `RedisMessageBus` | `IMessageBus` | Foundatio.Redis |
| `RedisFileStorage` | `IFileStorage` | Foundatio.Redis |
| `CacheLockProvider` | `ILockProvider` | Foundatio (with Redis cache) |

## Installation

```bash
dotnet add package Foundatio.Redis
```

## Connection Setup

### Basic Connection

```csharp
using StackExchange.Redis;

var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
```

### Production Connection

```csharp
var options = new ConfigurationOptions
{
    EndPoints = { "redis-primary:6379", "redis-replica:6379" },
    Password = "your-password",
    Ssl = true,
    AbortOnConnectFail = false,
    ConnectRetry = 5,
    ConnectTimeout = 5000,
    SyncTimeout = 5000,
    AsyncTimeout = 5000
};

var redis = await ConnectionMultiplexer.ConnectAsync(options);
```

### Connection String Format

```txt
redis:6379,password=secret,ssl=true,abortConnect=false
```

## RedisCacheClient

A distributed cache backed by Redis.

### Basic Usage

```csharp
using Foundatio.Caching;

var cache = new RedisCacheClient(options =>
{
    options.ConnectionMultiplexer = redis;
});

// Store and retrieve
await cache.SetAsync("user:123", user);
var cachedUser = await cache.GetAsync<User>("user:123");

// With expiration
await cache.SetAsync("session:abc", session, TimeSpan.FromHours(1));
```

### Configuration Options

```csharp
var cache = new RedisCacheClient(options =>
{
    options.ConnectionMultiplexer = redis;
    options.LoggerFactory = loggerFactory;
    options.Serializer = new SystemTextJsonSerializer();
    options.ReadMode = CommandFlags.PreferReplica; // Route reads to replicas
});
```

### Advanced Operations

```csharp
// Increment/Decrement
await cache.IncrementAsync("counter", 1);
await cache.IncrementAsync("views", 1.5);

// Set if not exists
var added = await cache.AddAsync("lock-key", "locked", TimeSpan.FromSeconds(30));

// Batch operations
await cache.SetAllAsync(new Dictionary<string, object>
{
    ["user:1"] = user1,
    ["user:2"] = user2
});

var users = await cache.GetAllAsync<User>(new[] { "user:1", "user:2" });
```

## RedisHybridCacheClient

Combines Redis (L2 distributed cache) with a local in-memory cache (L1) for optimal performance. This implements the industry-standard L1/L2 caching architecture.

### How It Works

**Read Flow:**
```txt
┌─────────┐     ┌──────────────┐     ┌──────────────┐
│ Request │────▶│  L1 Cache    │────▶│  L2 Cache    │
│         │     │ (In-Memory)  │     │   (Redis)    │
└─────────┘     └──────────────┘     └──────────────┘
                      │                      │
                      ▼                      ▼
                 Cache Hit?            Cache Hit?
                      │                      │
                  Yes: Return          Yes: Store in L1
                                       Then: Return
```

**Write Flow (Distributed-First):**
```txt
┌──────────────────┐
│ Write Operation  │
└────────┬─────────┘
         │
         ▼
  ┌──────────────┐
  │  L2 Cache    │  ◀── Write to L2 first (source of truth)
  │   (Redis)    │
  └──────┬───────┘
         │
         │ Success?
         │
   ┌─────┴─────┐
   │           │
   ▼           ▼
  Yes          No
   │           │
   ▼           │
┌──────────────┐ │
│  L1 Cache    │ │
│ (In-Memory)  │ │
└──────────────┘ │
   │           │
   └─────┬─────┘
         │
         ▼
  ┌──────────────┐
  │ Message Bus  │───▶ Other Instances:
  │ (Publish)    │     Clear SPECIFIC
  └──────────────┘     Keys Only
```

- On **read**: Check L1 (local) first, then L2 (Redis). Store in L1 if found in L2.
- On **write**: Write to L2 (Redis) first, then update L1 only on success, then publish invalidation so other instances clear **only the affected keys** from their L1 cache.
- **Prefix removal**: `RemoveByPrefixAsync("user:")` clears all `user:*` keys on all instances.
- **Full flush**: `RemoveAllAsync()` with no keys clears entire L1 cache on all instances.

::: warning Shared Message Bus Topic
By default, all instances share the same Redis pub/sub topic for invalidation. In high-write scenarios, consider using separate topics per feature area. See the [Caching Guide](/guide/caching#hybrid-cache-invalidation-traffic) for details.
:::

### Basic Usage

```csharp
var hybridCache = new RedisHybridCacheClient(
    redisConfig => redisConfig.ConnectionMultiplexer(redis).LoggerFactory(loggerFactory),
    localConfig => localConfig.MaxItems(1000)
);
```

### Cache Invalidation

The hybrid cache uses Redis pub/sub to invalidate local caches across all instances:

```csharp
// When you update a value, all instances are notified
await hybridCache.SetAsync("config:app", newConfig);

// Other instances automatically invalidate their local copy
```

### Benefits

- **Reduced Latency**: Local cache hits avoid network round-trips
- **Reduced Load**: Fewer Redis operations
- **Automatic Sync**: Pub/sub keeps caches consistent
- **Configurable Size**: Control local cache memory usage

## RedisQueue

A reliable distributed queue with visibility timeout and dead letter support.

### Basic Usage

```csharp
using Foundatio.Queues;

var queue = new RedisQueue<WorkItem>(options =>
{
    options.ConnectionMultiplexer = redis;
    options.Name = "work-items";
});

// Enqueue
await queue.EnqueueAsync(new WorkItem { Id = 1 });

// Dequeue and process
var entry = await queue.DequeueAsync();
try
{
    await ProcessAsync(entry.Value);
    await entry.CompleteAsync();
}
catch
{
    await entry.AbandonAsync();
}
```

### Configuration Options

```csharp
var queue = new RedisQueue<WorkItem>(options =>
{
    options.ConnectionMultiplexer = redis;
    options.Name = "work-items";

    // Visibility timeout
    options.WorkItemTimeout = TimeSpan.FromMinutes(5);

    // Retry settings
    options.Retries = 3;
    options.RetryDelay = TimeSpan.FromSeconds(30);

    // Dead letter settings
    options.DeadLetterTimeToLive = TimeSpan.FromDays(1);
    options.DeadLetterMaxItems = 100;

    // Run maintenance (cleanup dead letters)
    options.RunMaintenanceTasks = true;

    // Route reads to replicas (see Read Routing section for caveats)
    options.ReadMode = CommandFlags.PreferReplica;

    options.LoggerFactory = loggerFactory;
});
```

### Queue Features

```csharp
// Get queue statistics
var stats = await queue.GetQueueStatsAsync();
Console.WriteLine($"Queued: {stats.Queued}");
Console.WriteLine($"Working: {stats.Working}");
Console.WriteLine($"Dead Letter: {stats.Deadletter}");

// Process continuously
await queue.StartWorkingAsync(async (entry, token) =>
{
    await ProcessWorkItemAsync(entry.Value);
});
```

## RedisMessageBus

A pub/sub message bus using Redis for cross-process communication.

### Basic Usage

```csharp
using Foundatio.Messaging;

var messageBus = new RedisMessageBus(options =>
{
    options.Subscriber = redis.GetSubscriber();
});

// Subscribe
await messageBus.SubscribeAsync<OrderCreatedEvent>(async message =>
{
    await HandleOrderCreatedAsync(message);
});

// Publish
await messageBus.PublishAsync(new OrderCreatedEvent { OrderId = "123" });
```

### Configuration Options

```csharp
var messageBus = new RedisMessageBus(options =>
{
    options.Subscriber = redis.GetSubscriber();
    options.Topic = "myapp"; // Channel prefix
    options.LoggerFactory = loggerFactory;
    options.Serializer = serializer;
});
```

### Topic-Based Routing

```csharp
// Messages are published to channels based on type
// e.g., "myapp:OrderCreatedEvent"

// All instances subscribed to this type receive the message
await messageBus.SubscribeAsync<OrderCreatedEvent>(HandleOrder);
```

## RedisFileStorage

Store files in Redis (suitable for small files and caching scenarios).

### Basic Usage

```csharp
using Foundatio.Storage;

var storage = new RedisFileStorage(options =>
{
    options.ConnectionMultiplexer = redis;
});

// Save file
await storage.SaveFileAsync("config/settings.json", settingsJson);

// Read file
var content = await storage.GetFileContentsAsync("config/settings.json");
```

### Configuration Options

```csharp
var storage = new RedisFileStorage(options =>
{
    options.ConnectionMultiplexer = redis;
    options.LoggerFactory = loggerFactory;
    options.Serializer = serializer;
    options.ReadMode = CommandFlags.PreferReplica; // Route reads to replicas
});
```

::: warning
Redis file storage is best for small files or caching scenarios. For large files, consider Azure Blob Storage or S3.
:::

## Distributed Locks with Redis

Use `CacheLockProvider` with Redis for distributed locking.

### Basic Usage

```csharp
using Foundatio.Lock;

var locker = new CacheLockProvider(
    cache: redisCacheClient,
    messageBus: redisMessageBus
);

await using var lockHandle = await locker.AcquireAsync(
    resource: "order:123",
    timeUntilExpires: TimeSpan.FromMinutes(5),
    cancellationToken: token
);

if (lockHandle != null)
{
    // Exclusive access to order:123
    await ProcessOrderAsync("123");
}
```

### Lock Patterns

```csharp
// Try to acquire, fail fast
var lockHandle = await locker.AcquireAsync("resource");
if (lockHandle == null)
{
    throw new ResourceBusyException();
}

// Wait for lock with timeout
var lockHandle = await locker.AcquireAsync(
    resource: "resource",
    acquireTimeout: TimeSpan.FromSeconds(30)
);

// Extend lock duration
await lockHandle.RenewAsync(TimeSpan.FromMinutes(5));
```

## Complete Redis Setup

### Service Registration

```csharp
public static IServiceCollection AddFoundatioRedis(
    this IServiceCollection services,
    string connectionString)
{
    // Connection
    services.AddSingleton<IConnectionMultiplexer>(sp =>
        ConnectionMultiplexer.Connect(connectionString));

    // Cache (Hybrid for best performance)
    services.AddSingleton<ICacheClient>(sp =>
        new RedisHybridCacheClient(
            redisConfig => redisConfig
                .ConnectionMultiplexer(sp.GetRequiredService<IConnectionMultiplexer>())
                .LoggerFactory(sp.GetRequiredService<ILoggerFactory>()),
            localConfig => localConfig.MaxItems(1000)
        ));

    // Message Bus
    services.AddSingleton<IMessageBus>(sp =>
        new RedisMessageBus(options =>
        {
            options.Subscriber = sp
                .GetRequiredService<IConnectionMultiplexer>()
                .GetSubscriber();
            options.LoggerFactory =
                sp.GetRequiredService<ILoggerFactory>();
        }));

    services.AddSingleton<IMessagePublisher>(sp =>
        sp.GetRequiredService<IMessageBus>());
    services.AddSingleton<IMessageSubscriber>(sp =>
        sp.GetRequiredService<IMessageBus>());

    // Lock Provider
    services.AddSingleton<ILockProvider>(sp =>
        new CacheLockProvider(
            sp.GetRequiredService<ICacheClient>(),
            sp.GetRequiredService<IMessageBus>()));

    return services;
}

// Add queue
public static IServiceCollection AddRedisQueue<T>(
    this IServiceCollection services,
    string name) where T : class
{
    services.AddSingleton<IQueue<T>>(sp =>
        new RedisQueue<T>(options =>
        {
            options.ConnectionMultiplexer =
                sp.GetRequiredService<IConnectionMultiplexer>();
            options.Name = name;
            options.LoggerFactory =
                sp.GetRequiredService<ILoggerFactory>();
        }));

    return services;
}
```

### Usage

```csharp
var builder = WebApplication.CreateBuilder(args);

var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? "localhost:6379";

builder.Services.AddFoundatioRedis(redisConnection);
builder.Services.AddRedisQueue<WorkItem>("work-items");
builder.Services.AddRedisQueue<EmailMessage>("emails");
```

## Production Considerations

### Connection Resilience

```csharp
var options = new ConfigurationOptions
{
    EndPoints = { "redis:6379" },
    AbortOnConnectFail = false,  // Don't throw on startup
    ConnectRetry = 5,
    ReconnectRetryPolicy = new ExponentialRetry(5000)
};
```

### Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddRedis(redisConnection, name: "redis");
```

### Monitoring

```csharp
// Get server info
var server = redis.GetServer("redis:6379");
var info = await server.InfoAsync();

// Memory usage
var memory = info.FirstOrDefault(g => g.Key == "memory");
```

### Cluster Support

```csharp
var options = new ConfigurationOptions
{
    EndPoints =
    {
        "redis-1:6379",
        "redis-2:6379",
        "redis-3:6379"
    }
};

// StackExchange.Redis handles cluster topology automatically
var redis = await ConnectionMultiplexer.ConnectAsync(options);
```

## Best Practices

### 1. Connection Management

```csharp
// ✅ Singleton connection
services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect("redis:6379"));

// ❌ Creating new connections
var redis = ConnectionMultiplexer.Connect("redis:6379");
```

### 2. Key Naming

```csharp
// ✅ Hierarchical, descriptive keys
await cache.SetAsync("user:123:profile", profile);
await cache.SetAsync("order:456:items", items);

// ❌ Flat, ambiguous keys
await cache.SetAsync("123", profile);
```

### 3. Serialization

```csharp
// Use efficient serializers for large objects
var serializer = new MessagePackSerializer();
var cache = new RedisCacheClient(o => o.Serializer = serializer);
```

### 4. TTL Strategy

```csharp
// Always set expiration for cached data
await cache.SetAsync("data", value, TimeSpan.FromHours(1));

// Use sliding expiration for frequently accessed data
await cache.SetAsync("session", data,
    expiresIn: TimeSpan.FromMinutes(30));
```

## Read Routing (Replica Reads)

All Redis providers support a `ReadMode` option that controls how read operations are routed in a master-replica topology. By default, reads go to the master node (`CommandFlags.None`). Set `ReadMode` to `CommandFlags.PreferReplica` to distribute reads to replica nodes, reducing load on the master and improving read throughput.

### Configuration

```csharp
using StackExchange.Redis;

// Enable replica reads on cache
var cache = new RedisCacheClient(o => o
    .ConnectionMultiplexer(redis)
    .ReadMode(CommandFlags.PreferReplica));

// Enable replica reads on queue
var queue = new RedisQueue<WorkItem>(o => o
    .ConnectionMultiplexer(redis)
    .ReadMode(CommandFlags.PreferReplica));

// Enable replica reads on file storage
var storage = new RedisFileStorage(o => o
    .ConnectionMultiplexer(redis)
    .ReadMode(CommandFlags.PreferReplica));
```

`PreferReplica` is safe on single-node deployments -- it falls back to the master when no replica exists. Write operations always go to the master regardless of this setting. Distributed locks are not affected (all lock operations use writes or Lua scripts on the master).

### ReadMode Values

| Value | Behavior | Use case |
|-------|----------|----------|
| `CommandFlags.None` (default) | Read from master | Backward compatible; strict consistency |
| `CommandFlags.PreferReplica` | Read from replica if available, fall back to master | Recommended for master-replica topologies |
| `CommandFlags.DemandReplica` | Replica only; error if none available | Dedicated read-scaling scenarios |
| `CommandFlags.DemandMaster` | Master only; error if unavailable | Critical path operations |

### Operation Routing by Provider

| Provider | Operation | Routing |
|----------|-----------|---------|
| **RedisCacheClient** | `GetAsync`, `GetAllAsync`, `GetListAsync` | Via ReadMode |
| | `ExistsAsync`, `GetExpirationAsync` | Via ReadMode |
| | `SetAsync`, `RemoveAsync`, `IncrementAsync` | Always master |
| | `GetAllExpirationAsync` | Always master (Lua script) |
| | Lua scripts (`SetIfHigher`, `ReplaceIfEqual`, etc.) | Always master |
| **RedisQueue** | Internal payload/metadata reads | Via ReadMode |
| | Enqueue, dequeue, complete, abandon | Always master |
| | Maintenance (work list, wait list scans) | Always master |
| **RedisFileStorage** | `GetFileStreamAsync`, `GetFileInfoAsync`, `ExistsAsync` | Via ReadMode |
| | `GetFileListAsync` | Via ReadMode |
| | `SaveFileAsync`, `DeleteFileAsync` | Always master |
| **RedisMessageBus** | Pub/sub | N/A (not routable) |

### Replication Lag Considerations

::: warning
Redis/Valkey replication is asynchronous. When using `PreferReplica`, reads may return stale data during the replication lag window (typically sub-millisecond on AWS ElastiCache, but variable under load). Review the scenarios below before enabling replica reads.
:::

| Scenario | Risk | Impact |
|----------|------|--------|
| **Queue: dequeue payload read** | **High** | After enqueue writes a payload, a dequeue on another process reads it back. If the replica hasn't replicated yet, the payload is `null`, the item is removed from the work list, and the message is silently lost. |
| **Queue: abandon retry count** | Medium | The attempts counter is incremented on master, then read back during abandon. A stale replica read returns an old count, giving the item one extra retry before dead-lettering. |
| **Queue: maintenance renewal check** | Medium | Lock renewal writes a timestamp to master. Maintenance reads it to check timeout. A stale read may auto-abandon an item that was just renewed, causing spurious re-processing. |
| **Queue: maintenance wait time** | Low | Wait times for retry delays are read from cache. A stale read makes an item wait slightly longer before retry. |
| **File storage: rename after save** | Low-Medium | `RenameFileAsync` reads file content immediately after save. A stale replica read could miss the just-written content. |
| **Cache: sorted set expiration** | Low | Reads the highest score from a sorted set to determine TTL. A stale read sets a slightly inaccurate expiration. |
| **Distributed locks** | **None** | Lock acquire, release, and renewal all use writes or Lua scripts that execute on master. |

**Per-provider guidance:**

- **RedisCacheClient**: `PreferReplica` is safe for most read-heavy workloads. Risk exists only if you read a key immediately after writing it from a different process.
- **RedisQueue**: Use caution. Under very high throughput, dequeue can fail to read a just-enqueued payload, causing message loss. Consider keeping `CommandFlags.None` for queues processing critical work items.
- **RedisFileStorage**: Generally safe. The rename-after-save edge case is unlikely in practice.

## Next Steps

- [Azure Implementation](./azure) - Azure Storage and Service Bus
- [AWS Implementation](./aws) - S3 and SQS
- [In-Memory Implementation](./in-memory) - Local development

## GitHub Repository

- [Foundatio.Redis](https://github.com/FoundatioFx/Foundatio.Redis) - View source code and contribute
