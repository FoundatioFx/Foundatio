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
3. On a distributed hit, cache the value locally when its expiration and any concurrent writes allow it
4. Return value to caller

### Write Flow

A successful `SetAsync` follows this flow.

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

The operation determines which entries peers evict.

| Operation | Invalidation Scope |
|-----------|-------------------|
| `SetAsync("user:123", ...)` | Clears `user:123` on other instances |
| `SetAllAsync(dict)` | Clears all specified keys |
| `RemoveAsync("user:123")` | Clears `user:123` on other instances |
| `RemoveByPrefixAsync("user:")` | Clears `user:*` pattern on other instances |
| `RemoveAllAsync()` (no keys) | Clears **entire** local cache on all instances |

### Smart Cache Invalidation

Removal operations notify peers when L2 reports that it removed something.

| Operation | Publishes When |
|-----------|---------------|
| `RemoveAsync(key)` | Key existed and was removed |
| `RemoveIfEqualAsync(key, expected)` | Key existed with matching value and was removed |
| `RemoveAllAsync(keys)` | At least one key was removed |
| `RemoveAllAsync()` (flush) | At least one key was removed |
| `RemoveByPrefixAsync(prefix)` | At least one key matched and was removed |
| `ListRemoveAsync(key, values)` | At least one value was removed from the list |

`AddAsync` creates a key without sending an invalidation. If L2 independently evicts a key and a writer adds it again, a peer can keep serving its previous local value until expiration, eviction, or another invalidation.

## L1/L2 Cache Architecture

Each instance keeps its own L1 and shares L2 with the other instances.

| Tier | Name | Implementation | Characteristics |
|------|------|----------------|-----------------|
| **L1** | Local Cache | `InMemoryCacheClient` | Fast (no network), per-instance, volatile |
| **L2** | Distributed Cache | Redis, etc. | Shared across instances, source of truth |

### Consistency Model

Writes update L2 first. The writer updates its own L1 when it knows the resulting value and the write is still current.

L2 is the source of truth. An in-flight read can return the value it fetched before a concurrent write. If an invalidation arrives during a single-key read and leaves L1 empty, the client can fetch the key once more to warm L1 with a current value. Overlapping writes on the same client discard uncertain local values. The next cache miss reads L2.

Other clients can serve an existing local value until their invalidation arrives. If L2 commits a single-key write and the provider throws before returning its result, both the writer and its peers can keep their previous local values. Set a TTL to bound staleness in either case.

Peer invalidations interpret a trailing `*` as a prefix. Writing the literal key `account:*` also evicts a peer's local `account:42` entry. Its next read reloads the value from L2.

On a cold scalar or unpaged list read, L2 reports the same null TTL for a persistent key and a missing key. The client checks existence before storing a persistent local copy. If that check fails, the caller still gets the value read from L2 and the client skips the local fill. Bulk reads get existence and TTL from the same expiration lookup.

For a list read from L2, `GetListAsync` supplies the members and `GetExpirationAsync` supplies the whole key's TTL. If the members expire at different times, L1 can continue returning an expired member until the cached list expires or is invalidated. A list containing a permanent member can retain that stale value indefinitely.

::: info TTL Skew Between L1 and L2
When setting expiration times, there is a small timing skew between L1 and L2:

1. L2 (distributed cache) sets TTL at time T
2. Network latency and processing occur
3. L1 (local cache) sets TTL at time T + delta

This means L1 may expire slightly **after** L2, potentially serving stale data for a brief window (typically milliseconds). For most use cases, this is negligible. If sub-second TTL accuracy is critical, consider raising a PR.
:::

### Local Cache Synchronization Strategies

| L2 result | Writer's local cache |
|-----------|----------------------|
| A scalar write returns a known value | Stores the value if the write is still current. |
| `SetIfHigherAsync` or `SetIfLowerAsync` reports no change | Evicts the entry because the result does not identify the stored value. |
| A list add or removal reports partial success | Evicts the list so an unpaged read can reload all members. |
| A bulk set or expiration change throws after it may have updated some keys | Evicts all requested keys and attempts to notify peers before rethrowing the original error. |

A warm list can apply the changed members directly. On a cold `ListAddAsync`, the client can seed L1 when L2 reports the key was absent before the write. If that check fails, the distributed add still runs and L1 stays cold. An existing distributed list stays cold until an unpaged read fetches all its members.

### Edge Cases with Zero Values

When `IncrementAsync` returns zero, the writer evicts its local entry. This includes a valid increment from five to zero. The next cache miss reads the value from L2.

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
Each `SetAsync` sends one invalidation to the shared topic. At 1,000 successful sets per second, a topic with ten subscribed instances receives 10,000 deliveries per second across those instances. `AddAsync` and removals that find nothing skip the notification.
:::

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
