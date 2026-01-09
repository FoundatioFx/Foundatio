# Hybrid Cache

The `HybridCacheClient` combines a local in-memory cache with a distributed cache for maximum performance. It's ideal for read-heavy workloads where the same data is accessed frequently across multiple requests.

**GitHub**: [Foundatio](https://github.com/FoundatioFx/Foundatio) (included in core package)

## Overview

| Feature | Description |
|---------|-------------|
| **Local Cache** | Fast in-process memory (no network) |
| **Distributed Cache** | Shared state across instances (Redis, etc.) |
| **Invalidation** | Automatic key-specific invalidation via message bus |
| **Best For** | Read-heavy, low-write workloads |

## Installation

Hybrid cache is included in the core Foundatio package:

```bash
dotnet add package Foundatio
```

For Redis-based hybrid cache:

```bash
dotnet add package Foundatio.Redis
```

## How It Works

### Read Flow

```txt
┌─────────┐     ┌──────────────┐     ┌────────────────────┐
│ Request │────▶│ Local Cache  │────▶│ Distributed Cache  │
└─────────┘     └──────────────┘     └────────────────────┘
                      │                        │
                      ▼                        ▼
                 Cache Hit?              Cache Hit?
                      │                        │
                  Yes: Return            Yes: Store in Local
                                         Then: Return
```

1. Check local in-memory cache first (zero network latency)
2. On miss, check distributed cache (Redis, etc.)
3. If found in distributed cache, store in local cache for future requests
4. Return value to caller

### Write Flow

```txt
┌──────────────────┐
│ Write Operation  │
└────────┬─────────┘
         │
         ▼
  ┌────────────────────┐
  │ Distributed Cache  │  ◀── Write to L2 first (source of truth)
  │    (L2 - Update)   │
  └────────┬───────────┘
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
│ Local Cache  │ │
│ (L1 - Update)│ │
└──────────────┘ │
     │           │
     └─────┬─────┘
           │
           ▼
  ┌──────────────────┐
  │  Message Bus     │
  │ (Invalidation)   │
  └────────┬─────────┘
           │
           ▼
┌─────────────────────────┐
│   Other Hybrid Cache    │
│   Instances: Clear      │
│   SPECIFIC Keys Only    │
└─────────────────────────┘
```

1. Write to distributed cache first (L2 is the source of truth)
2. Only update local cache (L1) if distributed write succeeds
3. Publish invalidation message via message bus
4. **Other instances** receive message and clear **only the affected keys** from their local cache
5. Current instance ignores its own invalidation messages (filtered by `CacheId`)

### Key-Specific Invalidation

Invalidation is **surgical** - only the affected keys are cleared on other instances, not the entire cache:

| Operation | Invalidation Scope |
|-----------|-------------------|
| `SetAsync("user:123", ...)` | Clears `user:123` on other instances |
| `SetAllAsync(dict)` | Clears all specified keys |
| `RemoveAsync("user:123")` | Clears `user:123` on other instances |
| `RemoveByPrefixAsync("user:")` | Clears `user:*` pattern on other instances |
| `RemoveAllAsync()` (no keys) | Clears **entire** local cache on all instances |

### Smart Cache Invalidation

The hybrid cache optimizes message bus traffic by **only publishing invalidation messages when the distributed cache actually changed**. This reduces unnecessary network traffic and processing overhead.

| Operation | Publishes When |
|-----------|---------------|
| `RemoveAsync(key)` | Key existed and was removed |
| `RemoveIfEqualAsync(key, expected)` | Key existed with matching value and was removed |
| `RemoveAllAsync(keys)` | At least one key was removed |
| `RemoveAllAsync()` (flush) | At least one key was removed |
| `RemoveByPrefixAsync(prefix)` | At least one key matched and was removed |
| `ListRemoveAsync(key, values)` | At least one value was removed from the list |

**Example**: If you call `RemoveAsync("user:123")` but the key doesn't exist in the distributed cache, no invalidation message is published because there's nothing for other instances to clear.

This optimization is safe because:

1. **Distributed cache is the source of truth** - if a key doesn't exist there, it shouldn't exist in any local cache
2. **Local caches are eventually consistent** - expired entries are cleaned up naturally
3. **Redis handles expiration automatically** - expired keys are already removed, so `KeyDeleteAsync` returns `false` only when the key truly doesn't exist

## L1/L2 Cache Architecture

`HybridCacheClient` implements a two-tier caching architecture following industry-standard terminology:

| Tier | Name | Implementation | Characteristics |
|------|------|----------------|-----------------|
| **L1** | Local Cache | `InMemoryCacheClient` | Fast (no network), per-instance, volatile |
| **L2** | Distributed Cache | Redis, etc. | Shared across instances, source of truth |

This architecture is similar to Microsoft's `HybridCache` (.NET 9+) and other distributed caching solutions like EasyCaching.

### Consistency Model

`HybridCacheClient` uses a **write-through** pattern to ensure consistency:

1. **Distributed-first writes**: All write operations go to L2 (distributed cache) first
2. **Conditional local update**: L1 (local cache) is only updated if L2 succeeds
3. **Cross-instance invalidation**: Message bus notifies other instances to clear affected keys

This ensures that:
- L2 is always the **source of truth**
- L1 never contains data that doesn't exist in L2
- Failed distributed writes don't leave stale data in local cache

::: info TTL Skew Between L1 and L2
When setting expiration times, there is a small timing skew between L1 and L2:

1. L2 (distributed cache) sets TTL at time T
2. Network latency and processing occur
3. L1 (local cache) sets TTL at time T + delta

This means L1 may expire slightly **after** L2, potentially serving stale data for a brief window (typically milliseconds). For most use cases, this is negligible. If sub-second TTL accuracy is critical, consider raising a PR.
:::

### Local Cache Synchronization Strategies

Different operations use different strategies to keep L1 in sync with L2:

| Strategy | When Used | Operations |
|----------|-----------|------------|
| **Set on success** | When we know the exact value after the operation | `SetAsync`, `ReplaceAsync`, `IncrementAsync` |
| **Set on full success** | When all items in a batch succeed | `ListAddAsync`, `ListRemoveAsync` (when count matches) |
| **Remove to invalidate** | When the final value is uncertain or partial success | `SetIfHigherAsync`, `SetIfLowerAsync`, partial `ListAddAsync`/`ListRemoveAsync` |
| **Remove on failure** | When the operation fails (e.g., past expiration) | `SetAsync`, `SetAllAsync`, `ReplaceAsync`, `ReplaceIfEqualAsync` |

**Set on success** - Used when the operation's result is deterministic:

```csharp
// After successful distributed write, we know the exact value
await distributedCache.SetAsync(key, value);
await localCache.SetAsync(key, value);  // Same value, guaranteed consistent

// IncrementAsync returns the new value, so we can cache it
long newValue = await distributedCache.IncrementAsync(key, amount, TimeSpan.FromMinutes(5));
await localCache.SetAsync(key, newValue, TimeSpan.FromMinutes(5));  // Cache the authoritative value

// IncrementAsync with null expiration removes TTL (consistent with SetAsync)
long newValue = await distributedCache.IncrementAsync(key, amount, null);
await localCache.SetAsync(key, newValue, null);  // Both caches: no expiration
```

**Set on full success** - Used for batch operations when all items succeed:

```csharp
// ListAddAsync: if all items were added, update local cache
long added = await distributedCache.ListAddAsync(key, items, expiresIn);
if (added == items.Length)
    await localCache.ListAddAsync(key, items, expiresIn);  // Full success
else
    await localCache.RemoveAsync(key);  // Partial success - force re-fetch
```

**Remove to invalidate** - Used for conditional operations where we don't know the actual value:

```csharp
// SetIfHigherAsync: even when difference == 0, we don't know the actual current value
// We only know our value wasn't higher, not what the distributed cache contains
double difference = await distributedCache.SetIfHigherAsync(key, value, expiresIn);
if (difference > 0)
    await localCache.SetAsync(key, value, expiresIn);  // Value was updated
else
    await localCache.RemoveAsync(key);  // Value wasn't updated - force re-fetch
```

**Remove on failure** - Ensures local cache doesn't contain stale data when distributed operation fails:

```csharp
// If ReplaceAsync fails (e.g., past expiration removes the key), remove from local
bool replaced = await distributedCache.ReplaceAsync(key, value, expiresIn);
if (!replaced)
    await localCache.RemoveAsync(key);
```

This approach ensures consistency even when:
- Local and distributed caches have different values
- Conditional operations have partial success (e.g., list operations)
- Multiple instances are writing concurrently
- Operations fail due to past expiration

## Basic Usage

### With Generic HybridCacheClient

```csharp
using Foundatio.Caching;
using Foundatio.Messaging;

// Create dependencies
var distributedCache = new RedisCacheClient(o => o.ConnectionMultiplexer = redis);
var messageBus = new RedisMessageBus(o => o.Subscriber = redis.GetSubscriber());

// Create hybrid cache
var hybridCache = new HybridCacheClient(
    distributedCache,
    messageBus,
    new InMemoryCacheClientOptions { MaxItems = 1000 }
);

// First access: fetches from Redis, caches locally
var user = await hybridCache.GetAsync<User>("user:123");

// Subsequent access: returns from local cache (no network call)
var sameUser = await hybridCache.GetAsync<User>("user:123");
```

### With RedisHybridCacheClient (Convenience)

```csharp
using Foundatio.Redis.Cache;
using StackExchange.Redis;

var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");

// All-in-one Redis hybrid cache
var hybridCache = new RedisHybridCacheClient(
    redisConfig => redisConfig.ConnectionMultiplexer(redis),
    localConfig => localConfig.MaxItems(1000)
);
```

## Configuration

### Local Cache Options

The local cache is an `InMemoryCacheClient`. Configure via `localCacheOptions`:

```csharp
var hybridCache = new HybridCacheClient(
    distributedCache,
    messageBus,
    new InMemoryCacheClientOptions
    {
        // Maximum items (LRU eviction when exceeded)
        MaxItems = 1000,

        // Clone values to prevent reference sharing bugs
        // See: /guide/implementations/in-memory#clonevalues
        CloneValues = false,  // Default: false

        // Custom serializer for cloning
        Serializer = mySerializer,

        // Logger factory
        LoggerFactory = loggerFactory
    }
);
```

For memory-based eviction and other advanced options, see [In-Memory Implementation](/guide/implementations/in-memory).

### Message Bus Topic

::: warning Shared Topic
By default, all `HybridCacheClient` instances share the same message bus topic. In high-write scenarios, consider using separate message bus instances with different topics to isolate invalidation traffic by feature area (e.g., separate topics for user cache vs order cache).
:::

## Monitoring

Access local cache statistics:

```csharp
// Local cache hits (reads served from local cache)
Console.WriteLine($"Local cache hits: {hybridCache.LocalCacheHits}");

// Current local cache item count
Console.WriteLine($"Local cache count: {hybridCache.LocalCache.Count}");

// Number of invalidation messages received from other instances
Console.WriteLine($"Invalidation calls: {hybridCache.InvalidateCacheCalls}");
```

## Performance Considerations

### Message Bus Traffic

::: warning Shared Topic Traffic
`HybridCacheClient` publishes an invalidation message for **every write operation**. In high-write scenarios with a shared topic, this can generate significant traffic across all instances.
:::

**The problem:**

```csharp
// Each write publishes an InvalidateCache message to ALL instances
await hybridCache.SetAsync("key1", value1);  // 1 message to all
await hybridCache.SetAsync("key2", value2);  // 1 message to all
await hybridCache.SetAsync("key3", value3);  // 1 message to all
// 1000 writes = 1000 messages to ALL instances
```

**Impact:**

- With 10 instances and 1000 writes/second = 10,000 messages/second total
- Every instance processes every invalidation message (even if irrelevant)
- Can overwhelm Redis pub/sub or other message bus implementations

**Solutions:**

**1. Use separate scoped hybrid cache client/message bus topics per model type** (recommended):

Consider isolating invalidation traffic by using separate message bus instances with different topics for unrelated caching concerns.

**2. Use `HybridAwareCacheClient` for write-heavy services:**

```csharp
// Background processor that writes lots of data
// No local cache, just publishes invalidations
var processor = new HybridAwareCacheClient(redisCache, messageBus);

// Web servers that read data
// Has local cache, receives invalidations
var webCache = new HybridCacheClient(redisCache, messageBus);
```

**3. Batch writes when possible:**

```csharp
// ❌ Individual sets = N messages
foreach (var user in users)
    await cache.SetAsync($"user:{user.Id}", user);

// ✅ SetAllAsync = 1 message (with all keys)
await cache.SetAllAsync(users.ToDictionary(u => $"user:{u.Id}", u => u));
```

**4. Consider if you need hybrid caching:**

```csharp
// Write-heavy, read-once data: just use distributed cache
var cache = new RedisCacheClient(o => o.ConnectionMultiplexer = redis);

// Read-heavy, rarely-written data: hybrid is beneficial
var hybridCache = new HybridCacheClient(cache, messageBus);
```

### Local Cache Memory

The local cache can consume significant memory. Always configure limits:

```csharp
var hybridCache = new HybridCacheClient(
    distributedCache,
    messageBus,
    new InMemoryCacheClientOptions
    {
        MaxItems = 1000  // LRU eviction when exceeded
    }
);
```

For memory-based limits, see [In-Memory - Memory-Based Eviction](/guide/implementations/in-memory#memory-based-eviction).

## Related Interfaces

### IHybridCacheClient

Implemented by `HybridCacheClient`. Marker interface for caches that combine local and distributed storage with automatic invalidation.

### IHybridAwareCacheClient

Implemented by `HybridAwareCacheClient`. Wraps a distributed cache and publishes invalidation messages **without maintaining a local cache**:

```csharp
// Service that only writes (e.g., background processor)
var cacheWriter = new HybridAwareCacheClient(
    distributedCacheClient: redisCacheClient,
    messagePublisher: redisMessageBus
);

// Write goes to Redis AND notifies all HybridCacheClient instances
await cacheWriter.SetAsync("user:123", user);
// Other services using HybridCacheClient will clear their local "user:123" cache
```

**Use cases:**

- Background processors that write data but don't need local caching
- Services that need to notify `HybridCacheClient` instances to invalidate
- Write-heavy services where local caching would be wasteful

### IMemoryCacheClient

Marker interface for in-memory cache implementations. Used for type checking and DI scenarios:

```csharp
// Register specific implementation type
services.AddSingleton<IMemoryCacheClient, InMemoryCacheClient>();

// Inject when you specifically need in-memory behavior
public class MyService(IMemoryCacheClient localCache) { }
```

## When to Use Hybrid Cache

### ✅ Good Use Cases

- **Configuration data**: Rarely changes, frequently read
- **User profiles**: Read on every request, updated occasionally
- **Product catalogs**: High read volume, batch updates
- **Reference data**: Lookup tables, enums, static data
- **Session data**: Read-heavy with occasional updates

### ⚠️ Consider Alternatives

- **High-write workloads**: Use distributed cache only (no invalidation traffic)
- **Large objects**: Serialization overhead may outweigh benefits
- **Single instance**: Just use `InMemoryCacheClient`
- **Real-time data**: Invalidation latency may be unacceptable

## DI Registration

```csharp
// Register dependencies
services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect("localhost:6379"));

services.AddSingleton<ICacheClient>(sp =>
{
    var redis = sp.GetRequiredService<IConnectionMultiplexer>();
    return new RedisHybridCacheClient(
        redisConfig => redisConfig
            .ConnectionMultiplexer(redis)
            .LoggerFactory(sp.GetRequiredService<ILoggerFactory>()),
        localConfig => localConfig.MaxItems(1000)
    );
});
```

## Next Steps

- [In-Memory Implementation](/guide/implementations/in-memory) - Local cache configuration options
- [Redis Implementation](/guide/implementations/redis) - Distributed cache setup
- [Caching Guide](/guide/caching) - Core caching concepts and patterns
- [Serialization](/guide/serialization) - Serializer configuration and performance
