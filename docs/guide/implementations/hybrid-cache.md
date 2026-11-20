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
                  Yes: Return            Yes: Check TTL and
                                         warm L1 if current
```

1. Check local in-memory cache first (zero network latency)
2. On miss, check distributed cache (Redis, etc.)
3. On a distributed hit, cache the result locally if it is complete, its TTL is known, and no overlapping write invalidated it
4. Return value to caller

### Write Flow

For a successful `SetAsync`:

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
           │ Can L1 store the result?
           │
     ┌─────┴─────┐
     │           │
     ▼           ▼
   Yes        Evict key
     │           │
     ▼           │
┌──────────────┐ │
│ Local Cache  │ │
│ (L1 - Store)│ │
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

1. Write to the distributed cache (L2).
2. Store the confirmed value in the writer's local cache (L1) if the write is still current. Evict the local entry if a concurrent write makes the result uncertain.
3. Publish the key to the message bus. Other instances evict it when they receive the message. The writer ignores its own message using `CacheId`.

### Key-Specific Invalidation

Peers use the scope carried by the invalidation message. A trailing `*` denotes a prefix, including when the written key itself ends in `*`.

| Operation | Invalidation Scope |
|-----------|-------------------|
| `SetAsync("user:123", ...)` | Clears `user:123` on other instances |
| `SetAllAsync(dict)` | Clears all specified keys |
| `RemoveAsync("user:123")` | Clears `user:123` on other instances |
| `RemoveByPrefixAsync("user:")` | Clears `user:*` pattern on other instances |
| `RemoveAllAsync()` (no keys) | Clears **entire** local cache on all instances |

### Smart Cache Invalidation

Removal operations publish an invalidation when the distributed cache reports a removal.

| Operation | Publishes When |
|-----------|---------------|
| `RemoveAsync(key)` | Key existed and was removed |
| `RemoveIfEqualAsync(key, expected)` | Key existed with matching value and was removed |
| `RemoveAllAsync(keys)` | At least one key was removed |
| `RemoveAllAsync()` (flush) | At least one key was removed |
| `RemoveByPrefixAsync(prefix)` | At least one key matched and was removed |
| `ListRemoveAsync(key, values)` | At least one value was removed from the list |

For example, `RemoveAsync("user:123")` sends no message if L2 reports the key was missing. `AddAsync` also skips notification on success. If L2 independently evicts a key and a writer adds it again, peers can retain their earlier value until expiration, eviction or another invalidation.

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
2. **Conditional local update**: L1 stores a confirmed value while the operation is still current
3. **Cross-instance invalidation**: Message bus notifies other instances to clear affected keys

An in-flight read can return the value it fetched before a concurrent write. On the same client, an overlapping write or delivered invalidation prevents that read from leaving a stale local entry. A peer can still serve a warm value while its invalidation is in transit. If a provider commits a single-key mutation and loses the reply, both clients can retain their prior values until expiration or eviction.

On a cold scalar or unpaged list read, a null TTL can mean either a persistent key or a key that disappeared after the value was read. The client checks whether the key still exists before caching it locally. If the TTL or existence lookup fails, it returns the value it already read and leaves L1 empty.

A paged list read never populates L1 with a partial list. A full list read uses the whole key's TTL. When individual members expire earlier, L1 can retain those members until the list is evicted or expires.

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
| **Set on full success** | When all requested changes succeed and the full local list is known | `ListAddAsync`, `ListRemoveAsync` (when count matches) |
| **Remove to invalidate** | When the final value is uncertain or partial success | `SetIfHigherAsync`, `SetIfLowerAsync`, partial `ListAddAsync`/`ListRemoveAsync` |
| **Remove on failure** | When the operation reports failure (e.g., past expiration) | `SetAsync`, `SetAllAsync`, `ReplaceAsync`, `ReplaceIfEqualAsync` |

A cold `ListAddAsync` can seed L1 when L2 confirms that the key was absent before the add. If L2 already contained members, the next unpaged read fetches the complete list. A failed existence check still allows the distributed write.

If a bulk set or expiration change throws after a partial commit, the client evicts the requested keys and attempts to notify peers before rethrowing. A local write failure after L2 success also triggers eviction and the operation's usual notification.

### Edge Cases with Zero Values

The `IncrementAsync` operation has an edge case where L1 and L2 may temporarily be inconsistent when the result is 0:

**IncrementAsync returning 0:**
- If `IncrementAsync` returns 0 (e.g., incrementing 5 by -5, or creating a new key with amount 0), the key is removed from L1 rather than cached
- This is a conservative approach since 0 could indicate either a legitimate value or an error condition
- **Self-healing**: The next `GetAsync` will fetch from L2 and populate L1

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
`SetAsync` publishes an invalidation on each completed write. In high-write scenarios with a shared topic, this can generate significant traffic across all instances.
:::

**The problem:**

```csharp
// Each successful SetAsync publishes an InvalidateCache message to subscribers
await hybridCache.SetAsync("key1", value1);  // 1 message to all
await hybridCache.SetAsync("key2", value2);  // 1 message to all
await hybridCache.SetAsync("key3", value3);  // 1 message to all
// 1000 writes = 1000 messages to ALL instances
```

**Impact:**

- With 10 instances and 1000 writes/second = 10,000 messages/second total
- Each subscribed instance receives the message and the writer ignores its own
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
