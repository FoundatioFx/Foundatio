# Caching

Caching allows you to store and access data lightning fast, saving expensive operations to create or get data. Foundatio provides multiple cache implementations through the `ICacheClient` interface.

## The ICacheClient Interface

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Caching/ICacheClient.cs)

```csharp
public interface ICacheClient : IDisposable
{
    // Key operations
    Task<bool> RemoveAsync(string key);
    Task<bool> RemoveIfEqualAsync<T>(string key, T expected);
    Task<int> RemoveAllAsync(IEnumerable<string>? keys = null);
    Task<int> RemoveByPrefixAsync(string prefix);
    Task<bool> ExistsAsync(string key);

    // Get operations
    Task<CacheValue<T>> GetAsync<T>(string key);
    Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys);

    // Set operations
    Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null);
    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null);
    Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null);
    Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null);
    Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null);

    // Numeric operations
    Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null);
    Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null);
    Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null);
    Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null);
    Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null);
    Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null);

    // Expiration operations
    Task<TimeSpan?> GetExpirationAsync(string key);
    Task<IDictionary<string, TimeSpan?>> GetAllExpirationAsync(IEnumerable<string> keys);
    Task SetExpirationAsync(string key, TimeSpan expiresIn);
    Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations);

    // List operations
    Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) where T : notnull;
    Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values) where T : notnull;
    Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100) where T : notnull;
}
```

## Expiration (TTL) Behavior

Many cache methods accept an optional `expiresIn` parameter that controls the TTL (Time-To-Live) of cached items. Understanding its behavior is critical for correct cache usage.

### Quick Reference

The following describes key writes. Conditional operations and lists have additional rules described below.

| `expiresIn` Value | Behavior |
|-------------------|----------|
| `null` | Entry will not expire. **Removes any existing TTL** when the write is applied. |
| Positive `TimeSpan` ≥ 5ms | Entry expires after the specified duration from now. |
| Greater than 0 and less than 5ms | **Treated as already expired.** Key is removed, operation returns failure value. See [Minimum Expiration](#minimum-expiration) below. |
| Zero or negative | **Treated as already expired.** Key is removed, operation returns failure value. |
| `TimeSpan.MaxValue` | Entry will not expire (equivalent to `null`). |

### Minimum Expiration

Foundatio uses a **minimum expiration of 5 milliseconds** (`CacheClientExtensions.MinimumExpiration`) for cache writes. A shorter `expiresIn` is treated as already expired: key writes remove the key and return their failure value; `ListAddAsync` removes only the supplied values and returns `0`.

**Why 5ms?**

External cache providers—most notably Redis—represent TTLs as integers. StackExchange.Redis converts a `TimeSpan` to milliseconds via `(long)timeSpan.TotalMilliseconds`, which **truncates** the fractional part. A TTL of 0.9ms becomes `0`, and Redis rejects `SET key PX 0` with:

```
ERR invalid expire time in 'setex'
```

This truncation-to-zero can happen legitimately in production when computing `expiresAtUtc - DateTime.UtcNow` on a time very close to "now"—a common race condition in high-throughput systems. The 5ms floor provides a margin above the 1ms truncation boundary.

**Behavior summary:**

```csharp
// Below threshold: treated as expired (key is removed, returns false/0)
await cache.SetAsync("key", value, TimeSpan.FromTicks(1));          // 100ns < 5ms → expired
await cache.SetAsync("key", value, TimeSpan.FromMilliseconds(3));   // 3ms < 5ms → expired

// At threshold: accepted
await cache.SetAsync("key", value, TimeSpan.FromMilliseconds(5));   // 5ms == 5ms → succeeds

// Above threshold: accepted
await cache.SetAsync("key", value, TimeSpan.FromMilliseconds(100)); // 100ms > 5ms → succeeds
```

The constant is accessible for consumers that need to validate TTLs before calling cache methods:

```csharp
using Foundatio.Caching;

if (myExpiration < CacheClientExtensions.MinimumExpiration)
{
    // TTL too short; skip the cache operation or use a longer TTL
}
```

### TTL Behavior by Method

The table describes expiration when the operation is applied. For example, `AddAsync` with an existing live key or `ReplaceIfEqualAsync` with a mismatched expected value leaves that entry unchanged. Expirations below 5ms follow the removal behavior in the fourth column.

| Method | `null` expiresIn | Positive expiresIn ≥ 5ms | Below 5ms | Return on Failure |
|--------|------------------|-------------------------|-----------|-------------------|
| `SetAsync` | No TTL (removes existing) | Sets TTL | Removes key | `false` |
| `AddAsync` | No TTL | Sets TTL | Removes key | `false` |
| `SetAllAsync` | No TTL (removes existing) | Sets TTL | Removes all supplied keys | `0` |
| `ReplaceAsync` | No TTL (removes existing) | Sets TTL | Removes key | `false` |
| `ReplaceIfEqualAsync` | No TTL (removes existing) | Sets TTL | Removes key | `false` |
| `IncrementAsync` | No TTL (removes existing) | Sets/updates TTL | Removes key | `0` |
| `SetIfHigherAsync` | No TTL (removes existing)* | Sets TTL* | Removes key | `0` |
| `SetIfLowerAsync` | No TTL (removes existing)* | Sets TTL* | Removes key | `0` |
| `ListAddAsync` | Supplied values do not expire | Sets per-value TTL | Removes supplied values | `0` |

\* **In-memory numeric conditions**: `InMemoryCacheClient` applies the requested expiration even when `SetIfHigherAsync` or `SetIfLowerAsync` leaves the numeric value unchanged. A return value of `0` does not imply that the TTL was preserved. Passing `null` clears the TTL. Updates rejected by size validation leave the existing entry unchanged. See [Conditional Expiration Behavior](#conditional-expiration-behavior) for an example.

::: tip ListRemoveAsync
`ListRemoveAsync` does not accept an `expiresIn` parameter. In-memory list removal recomputes the key's expiration from the remaining values: any non-expiring value keeps the key non-expiring; otherwise the latest remaining expiration is used. Removing the last value removes the key.
:::

::: info Integer vs Floating-Point Increments
`IncrementAsync` supports both integer (`long`) and floating-point (`double`) amounts. Use the same numeric type consistently for a counter, especially when fractional values are possible:

```csharp
// Integer increments
await cache.IncrementAsync("counter", 1L, TimeSpan.FromHours(1));
await cache.IncrementAsync("counter", 5L, TimeSpan.FromHours(1));

// Floating-point increments, including whole-number amounts
await cache.IncrementAsync("score", 1d, TimeSpan.FromHours(1));
await cache.IncrementAsync("score", 1.5d, TimeSpan.FromHours(1));
await cache.IncrementAsync("score", 2d, TimeSpan.FromHours(1)); // Total: 4.5
```

For Redis implementations, integer amounts (including `2.0` where the fractional part is zero) use the more efficient `INCRBY` command, while fractional amounts use `INCRBYFLOAT`.
:::

### Detailed Examples

```csharp
// Basic Set Operations

// No expiration - item lives until explicitly removed or evicted
await cache.SetAsync("permanent-key", value);           // null is default
await cache.SetAsync("also-permanent", value, null);    // explicit null

// Expires in 30 minutes
await cache.SetAsync("session", data, TimeSpan.FromMinutes(30));

// Never expires (equivalent to null)
await cache.SetAsync("config", settings, TimeSpan.MaxValue);

// Zero/negative = expired, key removed, returns false
var success = await cache.SetAsync("invalid", value, TimeSpan.Zero);        // false
var alsoFails = await cache.SetAsync("invalid", value, TimeSpan.FromSeconds(-1)); // false


// Increment Operations (TTL Behavior)

// Create counter with TTL
await cache.SetAsync("counter", 0, TimeSpan.FromMinutes(5));

// Increment with null removes TTL (consistent with SetAsync)
await cache.IncrementAsync("counter", 1, null);  // TTL removed!

// Increment with explicit TTL sets it
await cache.IncrementAsync("counter", 1, TimeSpan.FromMinutes(10)); // TTL now 10 min

// Zero/negative removes key, returns 0
var result = await cache.IncrementAsync("counter", 5, TimeSpan.Zero); // 0


// SetIfHigher/SetIfLower (TTL Removal)

// Create with TTL
await cache.SetAsync("max-users", 100, TimeSpan.FromHours(1));

// Update without TTL - REMOVES the existing TTL
await cache.SetIfHigherAsync("max-users", 150, null); // No TTL now!

// Update with TTL - sets new TTL
await cache.SetIfHigherAsync("max-users", 200, TimeSpan.FromHours(2)); // TTL = 2 hours

// Zero/negative removes key, returns 0
var diff = await cache.SetIfHigherAsync("max-users", 999, TimeSpan.Zero); // 0
```

### Managing Expiration

```csharp
// Check remaining TTL
TimeSpan? ttl = await cache.GetExpirationAsync("session");
if (ttl == null)
{
    // Key doesn't exist OR has no expiration
}

// Update expiration on existing key
await cache.SetExpirationAsync("session", TimeSpan.FromMinutes(30));

// Remove expiration (make permanent) - use SetAllExpirationAsync with null
await cache.SetAllExpirationAsync(new Dictionary<string, TimeSpan?>
{
    ["session"] = null  // Removes TTL, key becomes permanent
});

// Bulk get/set expirations
var ttls = await cache.GetAllExpirationAsync(new[] { "key1", "key2", "key3" });
await cache.SetAllExpirationAsync(new Dictionary<string, TimeSpan?>
{
    ["key1"] = TimeSpan.FromMinutes(10),
    ["key2"] = TimeSpan.FromHours(1),
    ["key3"] = null  // Remove expiration
});
```

::: Warning Azure Managed Redis
On Azure Managed Redis (and many Redis deployments), the default eviction policy is `volatile-lru`, meaning **only keys with a TTL are eligible for eviction**. If you create many non-expiring keys, you may experience memory pressure and write failures.

**Recommendations:**

- Always set appropriate TTLs for cache entries when possible
- Use `TimeSpan.MaxValue` only when you explicitly need non-expiring cache entries; it does not make cached data durable
- Monitor your Redis memory usage and eviction metrics

**Further Reading:**

- [Azure Managed Cache for Redis eviction policies](https://docs.microsoft.com/en-us/azure/azure-cache-for-redis/cache-configure#memory-policies)
- [Redis eviction policies documentation](https://redis.io/docs/reference/eviction/)
:::

## Implementations

### InMemoryCacheClient

An in-memory cache implementation (L1 cache) valid for the lifetime of the process. See the [In-Memory Implementation Guide](./implementations/in-memory) for detailed configuration options including memory-based eviction.

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Caching/InMemoryCacheClient.cs)

```csharp
using Foundatio.Caching;

var cache = new InMemoryCacheClient();

// Basic operations
await cache.SetAsync("key", "value");
var result = await cache.GetAsync<string>("key");

// With expiration
await cache.SetAsync("session", sessionData, TimeSpan.FromMinutes(30));

// With item limits (LRU eviction)
var limitedCache = new InMemoryCacheClient(o => o.MaxItems(1000));
```

#### In-memory update behavior

Conditional writes and list updates publish replacement entries atomically. An oversized update rejected by size validation preserves the previous value, expiration, and tracked size.

Every write treats an expired entry as missing, even before background maintenance removes it, matching Redis: `IncrementAsync`, `SetIfHigherAsync`, `SetIfLowerAsync` and `ListAddAsync` start fresh, `ReplaceAsync`, `SetExpirationAsync` and `ListRemoveAsync` find nothing to change, and `RemoveIfEqualAsync` and `ReplaceIfEqualAsync` return `false` rather than letting a stale owner remove or renew it. A write that finds an expired entry reclaims it and raises `ItemExpired` once, without touching a concurrent replacement.

Successful removal of a live matching value, or removal of the last list value, frees tracked memory immediately and does not raise an expiration event.

List updates preserve the collection structure of previously returned snapshots. They preserve the comparers of `Dictionary`, `SortedDictionary`, `SortedList`, `ConcurrentDictionary`, and, on .NET 9 or later, `OrderedDictionary`. Other `IDictionary` implementations throw `NotSupportedException` when an update requires copying them; the original entry remains unchanged.

`Items` returns cached values without changing eviction order. Both `Items` and ordinary reads honor `CloneValues`. With cloning disabled, mutable payloads and list elements can remain shared: a collection snapshot is not a deep copy. Do not mutate shared objects concurrently with cache operations.

::: tip Large list updates
Batch values into a single `ListAddAsync` or `ListRemoveAsync` call. Lists over 32 items are stored as immutable snapshots, so a small change copies only the parts it touches, with or without a size calculator. Cloning and raw dictionary access (`GetAsync<IDictionary<T, DateTime?>>`) can still require copies proportional to the list size. Benchmark representative list sizes and read/write patterns before choosing these options; atomic updates do not make every workload allocation-free.

A custom `SizeCalculator` receives list values as a read-only `IDictionary<T, DateTime?>` (item to expiration), not a concrete `Dictionary<T, DateTime?>`. Check for the interface, not the concrete type, and don't modify it.
:::

### HybridCacheClient

Combines a local in-memory cache (L1) with a distributed cache (L2) for maximum performance. This implements the industry-standard L1/L2 caching architecture, ideal for read-heavy workloads where the same data is accessed frequently across multiple requests.

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Caching/HybridCacheClient.cs)

```csharp
using Foundatio.Caching;

var hybridCache = new HybridCacheClient(
    redisCacheClient,
    redisMessageBus,
    new InMemoryCacheClientOptions { MaxItems = 1000 }
);

// First access: fetches from Redis, caches locally
var user = await hybridCache.GetAsync<User>("user:123");

// Subsequent access: returns from local cache (no network call)
var sameUser = await hybridCache.GetAsync<User>("user:123");
```

**Key features:**

- **Read-through**: L1 (local) cache miss falls back to L2 (distributed) cache
- **Write-through**: Writes go to L2 first, then L1 only on success (distributed-first pattern)
- **Key-specific invalidation**: Only affected keys are cleared on other instances
- **Message bus coordination**: Automatic invalidation across all hybrid cache instances

::: tip Full Documentation
See [Hybrid Cache Implementation](/guide/implementations/hybrid-cache) for detailed configuration, performance considerations, and best practices.
:::

### ScopedCacheClient

Prefix all cache keys for easy namespacing:

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Caching/ScopedCacheClient.cs)

```csharp
using Foundatio.Caching;

var cache = new InMemoryCacheClient();
var tenantCache = new ScopedCacheClient(cache, "tenant:abc");

// All keys automatically prefixed
await tenantCache.SetAsync("settings", settings);  // Key: "tenant:abc:settings"
await tenantCache.SetAsync("users", users);        // Key: "tenant:abc:users"

// Clear all keys for this tenant
await tenantCache.RemoveByPrefixAsync("");  // Removes tenant:abc:*
```

**Use cases:**

- Multi-tenant applications
- Feature-specific caches
- Test isolation

### RedisCacheClient

Distributed cache using Redis (separate package):

[View source](https://github.com/FoundatioFx/Foundatio.Redis/blob/main/src/Foundatio.Redis/Cache/RedisCacheClient.cs)

```csharp
// dotnet add package Foundatio.Redis

using Foundatio.Redis.Cache;
using StackExchange.Redis;

var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var cache = new RedisCacheClient(o => o.ConnectionMultiplexer(redis));

await cache.SetAsync("user:123", user, TimeSpan.FromHours(1));
```

### RedisHybridCacheClient

Combines `RedisCacheClient` with `HybridCacheClient`:

[View source](https://github.com/FoundatioFx/Foundatio.Redis/blob/main/src/Foundatio.Redis/Cache/RedisHybridCacheClient.cs)

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var hybridCache = new RedisHybridCacheClient(
    redisConfig => redisConfig.ConnectionMultiplexer(redis),
    localConfig => localConfig.MaxItems(1000)
);
```

## Cache Interface Hierarchy

Foundatio provides several cache interfaces for different use cases:

```
ICacheClient (base interface)
├── IMemoryCacheClient (in-memory specific)
├── IHybridCacheClient (local + distributed)
└── IHybridAwareCacheClient (distributed with invalidation)
```

### IHybridCacheClient

Implemented by `HybridCacheClient` ([view source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Caching/HybridCacheClient.cs)). Combines a local in-memory cache (L1) with a distributed cache (L2). When you write data, it:

1. Writes to L2 (distributed cache) first - the source of truth
2. Updates L1 (local cache) only if L2 succeeds
3. Publishes an invalidation message via `IMessageBus`

When you read data:

1. Checks L1 (local cache) first (fast, no network)
2. Falls back to L2 (distributed cache) on miss
3. Populates L1 with result

### IHybridAwareCacheClient

Implemented by `HybridAwareCacheClient` ([view source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Caching/HybridAwareCacheClient.cs)). Wraps a distributed cache (L2) and publishes invalidation messages **without maintaining a local cache (L1)**. Use this when:

- You have a service that only writes to cache (e.g., background processor)
- You want to notify `HybridCacheClient` instances to invalidate their L1 caches
- You don't need local caching on this particular service

```csharp
// Service that writes data but doesn't need local caching
var cacheWriter = new HybridAwareCacheClient(
    distributedCacheClient: redisCacheClient,
    messagePublisher: redisMessageBus
);

// Write goes to Redis AND notifies all HybridCacheClient instances
await cacheWriter.SetAsync("user:123", user);

// Other services using HybridCacheClient will clear their local "user:123" cache
```

### IMemoryCacheClient

A marker interface that identifies in-memory cache implementations (e.g., `InMemoryCacheClient`). This interface is used for type checking and dependency injection scenarios where you need to distinguish between L1 (in-memory) and L2 (distributed) cache implementations.

```csharp
// Register specific implementation type
services.AddSingleton<IMemoryCacheClient, InMemoryCacheClient>();

// Inject when you specifically need in-memory behavior
public class MyService(IMemoryCacheClient localCache) { }
```

## Performance Considerations

### Serialization and Cloning Overhead

Distributed caches serialize values for storage and deserialize them on reads. `InMemoryCacheClient` stores objects directly and optionally deep-clones mutable payloads; it does not use a JSON round trip for cloning.

- **With `CloneValues = true`**: mutable payloads are copied when stored and returned, adding allocation and copying costs.
- **With `CloneValues = false`** (the default): payloads can be shared by reference. This avoids cloning costs, but callers must manage mutation safely.

### Value Cloning {#clonevalues}

The `CloneValues` option controls whether cached values are cloned on read and write operations. This is critical for preventing reference sharing bugs.

**Default: `false`** (no cloning, direct reference storage)

#### The Problem: Reference Sharing

Without cloning, the cache stores direct references to objects. Mutating a shared object changes the value seen by subsequent reads until the entry is replaced or removed:

```csharp
var cache = new InMemoryCacheClient(); // CloneValues = false (default)

var user = new User { Name = "Alice", Balance = 100.0m };
await cache.SetAsync("user:1", user);

// Get from cache and accidentally mutate
var cached = (await cache.GetAsync<User>("user:1")).Value;
cached.Balance = 0.0m;  // ⚠️ Mutates the cached object!

// Later reads return the MUTATED value
var again = (await cache.GetAsync<User>("user:1")).Value;
Console.WriteLine(again.Balance); // 0.0 (not 100.0!)
```

This is especially dangerous when:
- Multiple code paths access the same cached data
- Objects are passed through layers (controllers → services → repositories)
- Async code shares cached instances across concurrent requests

#### The Solution: Enable Cloning

```csharp
var cache = new InMemoryCacheClient(o => o.CloneValues(true));

var user = new User { Name = "Alice", Balance = 100.0m };
await cache.SetAsync("user:1", user);

// Mutations are isolated to this copy
var cached = (await cache.GetAsync<User>("user:1")).Value;
cached.Balance = 0.0m;  // Only affects this instance

// Fresh reads get original value
var fresh = (await cache.GetAsync<User>("user:1")).Value;
Console.WriteLine(fresh.Balance); // 100.0 ✓
```

**How it works:**

`InMemoryCacheClient` uses deep cloning for payloads that require it. `SetAsync` stores a copy and `GetAsync` returns a copy; `Items` also honors this setting. Changing the configured JSON serializer does not change this cloning mechanism. Primitive values and strings do not require a deep copy.

#### Performance Trade-offs

| Operation | `CloneValues = false` | `CloneValues = true` |
|-----------|----------------------|---------------------|
| `SetAsync` | Store the payload without cloning | Deep-copy mutable payloads before storage |
| `GetAsync` | Return the stored payload without cloning | Deep-copy mutable payloads before returning them |
| Payload memory | May share one instance | Additional copies for stored and returned values |

Cloning cost depends on the payload's object graph, collection sizes, and access pattern. Measure representative workloads instead of assuming a fixed per-operation overhead. List updates may also copy collection storage independently of payload cloning.

#### When to Enable Cloning

✅ **Enable `CloneValues = true` when:**
- Caching mutable objects (DTOs, entities, view models)
- Multiple code paths access the same cached data
- You can't guarantee code won't mutate cached objects
- Working with shared state across async operations
- Debugging unexplained cache corruption

❌ **Keep `CloneValues = false` when:**
- Caching strings, primitives, or object graphs that are genuinely immutable
- You have strict control over mutation and synchronization
- Performance is critical and you can guarantee immutability
- Using immutable collections whose contained values are also immutable

An `init`-only property or a record does not make referenced lists or other mutable objects immutable. Likewise, an immutable collection does not make its elements immutable.

#### Best Practices

**Pattern 1: Use immutable values (no cloning needed):**

```csharp
var cache = new InMemoryCacheClient(); // CloneValues = false
await cache.SetAsync("user:1", new UserDto(1, "Alice", 100.0m));

// This record contains only immutable values.
public record UserDto(int Id, string Name, decimal Balance);
```

**Pattern 2: Clone when mutability is unavoidable:**

```csharp
var cache = new InMemoryCacheClient(o => o.CloneValues(true));
await cache.SetAsync("user:1", userEntity);
// Mutations to userEntity or a returned copy do not change the stored copy.

public class UserEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = String.Empty;
    public decimal Balance { get; set; }
}
```

**Pattern 3: Mix both strategies:**

```csharp
// Separate caches for different needs
var immutableCache = new InMemoryCacheClient(o => o.CloneValues(false));
var mutableCache = new InMemoryCacheClient(o => o.CloneValues(true));

// Immutable config
await immutableCache.SetAsync("config:theme", "dark");

// Mutable user data
await mutableCache.SetAsync("user:1", userEntity);
```

### Hybrid Cache Performance

For `HybridCacheClient`-specific performance considerations including message bus traffic, memory pressure, and optimization strategies, see [Hybrid Cache - Performance Considerations](/guide/implementations/hybrid-cache#performance-considerations).

## Common Patterns

### Cache-Aside Pattern

The most common caching pattern:

```csharp
public async Task<User> GetUserAsync(int userId)
{
    var cacheKey = $"user:{userId}";

    // Try cache first
    var cached = await _cache.GetAsync<User>(cacheKey);
    if (cached.HasValue)
        return cached.Value;

    // Load from database
    var user = await _database.GetUserAsync(userId);

    // Cache for future requests
    await _cache.SetAsync(cacheKey, user, TimeSpan.FromMinutes(30));

    return user;
}
```

### Cache Stampede Protection

The cache-aside pattern above is vulnerable to **cache stampedes** (also called the thundering herd problem). When a popular key expires, many concurrent requests all see a cache miss simultaneously and each independently loads the same data from the backing store. For an expensive query that takes two seconds to run, 50 concurrent callers means 50 identical database queries instead of one.

Use [`CacheLockProvider`](/guide/locks) to serialize cache regeneration so only one caller loads the data while others wait:

```csharp
public class ProductService
{
    private readonly ICacheClient _cache;
    private readonly ILockProvider _locker;

    public ProductService(ICacheClient cache, ILockProvider locker)
    {
        _cache = cache;
        _locker = locker;
    }

    public async Task<Product?> GetProductAsync(int productId, CancellationToken ct)
    {
        var cacheKey = $"product:{productId}";

        var cached = await _cache.GetAsync<Product>(cacheKey);
        if (cached.HasValue)
            return cached.Value;

        // Only one caller regenerates; others wait for the lock then re-check cache.
        await using var lck = await _locker.TryAcquireAsync(
            $"cache-load:{cacheKey}",
            timeUntilExpires: TimeSpan.FromSeconds(30),
            cancellationToken: ct);

        if (lck is null)
            return null; // Could not acquire -- caller decides how to handle

        // Double-check: another caller may have populated the cache while we waited.
        cached = await _cache.GetAsync<Product>(cacheKey);
        if (cached.HasValue)
            return cached.Value;

        var product = await _database.GetProductAsync(productId);
        await _cache.SetAsync(cacheKey, product, TimeSpan.FromMinutes(30));
        return product;
    }
}
```

The key points of this pattern:

1. **Lock on the cache key.** Use a lock name derived from the cache key (e.g., `cache-load:product:42`) so different keys are loaded concurrently while the same key is serialized.
2. **Double-check after acquiring.** Another caller may have populated the cache while you were waiting for the lock. Always re-read before loading from the backing store.
3. **Lock expiration as a safety net.** Set `timeUntilExpires` to a value longer than the expected load time. If the loader crashes, the lock auto-expires and the next caller retries.

::: tip CacheLockProvider + IMessageBus
When `CacheLockProvider` is configured with an `IMessageBus`, waiting callers are notified instantly via pub/sub when the lock is released. Without a message bus, lock release falls back to polling. For stampede protection where multiple callers are blocked on the same lock, the message bus significantly reduces wait time.
:::

### Atomic Operations

Use conditional operations for race-safe updates:

```csharp
// Only set if key doesn't exist
bool added = await cache.AddAsync("lock:resource", "owner-id");

// Replace only if value matches expected
bool replaced = await cache.ReplaceIfEqualAsync("counter", 2, 1);

// Atomic increment
long newValue = await cache.IncrementAsync("page-views", 1);
```

### Counter Patterns

Track metrics with atomic operations:

```csharp
// Increment counters
await cache.IncrementAsync("api:calls:today", 1);
await cache.IncrementAsync("user:123:login-count", 1);

// Track high-water marks
await cache.SetIfHigherAsync("max-concurrent-users", currentUsers);

// Track minimums
await cache.SetIfLowerAsync("fastest-response-ms", responseTime);
```

#### SetIfHigher/SetIfLower Return Values

These methods return the **difference** between the new and old values, not the new value itself:

```csharp
// Key doesn't exist - returns the value itself (difference from 0)
double diff = await cache.SetIfHigherAsync("max-users", 100); // Returns 100

// Value is higher - returns the delta
diff = await cache.SetIfHigherAsync("max-users", 150); // Returns 50 (150 - 100)

// Value is NOT higher - returns 0 (numeric value is unchanged)
diff = await cache.SetIfHigherAsync("max-users", 120); // Returns 0

// Read the current value separately; another writer may have changed it in the meantime.
var currentMax = (await cache.GetAsync<double>("max-users")).Value;
```

#### Conditional Expiration Behavior

For `InMemoryCacheClient`, `SetIfHigherAsync` and `SetIfLowerAsync` apply `expiresIn` even when the numeric condition is not met. This differs from a rejected `ReplaceIfEqualAsync`, which leaves the entry unchanged. Do not infer expiration behavior solely from a numeric return value of `0`.

```csharp
using var cache = new InMemoryCacheClient();

// Set with a 1-hour TTL
await cache.SetIfHigherAsync("max-users", 100L, TimeSpan.FromHours(1));

// Numeric value stays 100, but the TTL is reset to 2 hours.
long difference = await cache.SetIfHigherAsync("max-users", 50L, TimeSpan.FromHours(2));
// difference == 0

// Numeric value still stays 100; passing null removes the TTL.
await cache.SetIfHigherAsync("max-users", 50L, null);
```

An in-memory update rejected by size validation does not apply its new expiration. Check the behavior of the cache provider you use rather than assuming every provider treats an unmet numeric condition identically.

### List Operations

Foundatio lists support **per-value expiration**, where each item in the list can have its own independent TTL. This is different from standard cache keys where expiration applies to the entire key.

List values are unique according to the list's comparer. Adding an existing value updates that value's expiration rather than creating a duplicate. Do not use separately paged reads as a consistent snapshot while other callers are updating the list.

#### Why Per-Value Expiration?

A sliding TTL for the whole list can retain old values indefinitely when new values are continually added. Per-value expiration lets old values expire without extending their lifetime every time another value is added:

```csharp
// Each supplied value expires after 7 days.
await cache.ListAddAsync("deleted-items", [itemId], TimeSpan.FromDays(7));
// Adding a different item later does not refresh itemId's expiration.
```

**Real-world use cases:**

- **Soft-delete tracking**: Track deleted document IDs that should be filtered from queries
- **Recent activity feeds**: Each activity expires independently (e.g., "active in last 5 minutes")
- **Rate limiting windows**: Track individual requests with their own expiration
- **Session tracking**: Track user sessions where each session has its own timeout

#### Basic List Usage

```csharp
// Add items with per-value expiration (each item expires in 1 hour)
await cache.ListAddAsync("user:123:recent-searches", new[] { "query1" }, TimeSpan.FromHours(1));
await cache.ListAddAsync("user:123:recent-searches", new[] { "query2" }, TimeSpan.FromHours(1));

// Items expire independently - query1 expires 1 hour after it was added,
// query2 expires 1 hour after IT was added (not when query1 was added)

// Get the first page (pages are one-based; expired items are filtered).
var searches = await cache.GetListAsync<string>(
    "user:123:recent-searches",
    page: 1,
    pageSize: 10
);

// Remove specific items from list
await cache.ListRemoveAsync("user:123:recent-searches", new[] { "query1" });
```

#### List Expiration Behavior

| `expiresIn` Value | Behavior |
|-------------------|----------|
| `null` | Supplied values do not expire. In memory, any non-expiring value keeps the key non-expiring. |
| Positive `TimeSpan` ≥ 5ms | Each supplied value expires independently after this duration. |
| Below 5ms, including zero or negative | The supplied values are removed from the list if present; `ListAddAsync` returns `0`. |

In-memory list updates recompute the key's expiration from the remaining values. If every remaining value expires, the key uses the latest expiration. An empty list is removed. Removing values does not refresh the expiration of values left in the list.

### Bulk Operations

Efficiently work with multiple keys:

```csharp
// Get multiple values
var keys = new[] { "user:1", "user:2", "user:3" };
var users = await cache.GetAllAsync<User>(keys);

// Set multiple values
var values = new Dictionary<string, User>
{
    ["user:1"] = user1,
    ["user:2"] = user2,
};
await cache.SetAllAsync(values, TimeSpan.FromHours(1));

// Remove multiple keys
await cache.RemoveAllAsync(keys);
```

## Dependency Injection

### Basic Registration

```csharp
// In-memory (development)
services.AddSingleton<ICacheClient, InMemoryCacheClient>();

// With options
services.AddSingleton<ICacheClient>(sp =>
    new InMemoryCacheClient(o => o.MaxItems(1000)));

// Redis (production)
services.AddSingleton<ICacheClient>(sp =>
    new RedisCacheClient(o => o.ConnectionMultiplexer(redis)));
```

### Hybrid with DI

```csharp
services.AddSingleton<ICacheClient>(sp =>
{
    var redis = sp.GetRequiredService<IConnectionMultiplexer>();
    return new HybridCacheClient(
        new RedisCacheClient(o => o.ConnectionMultiplexer(redis)),
        sp.GetRequiredService<IMessageBus>()
    );
});
```

### Named Caches

Use different caches for different purposes:

```csharp
services.AddKeyedSingleton<ICacheClient>("session",
    new InMemoryCacheClient(o => o.MaxItems(10000)));
services.AddKeyedSingleton<ICacheClient>("geo",
    new InMemoryCacheClient(o => o.MaxItems(250)));
```

## Testing Expiration

Use `FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package instead of sleeping or creating a custom clock. Inject the same provider into the cache and any collaborating lock provider or service that depends on time.

```csharp
using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class CacheExpirationTests
{
    [Fact]
    public async Task GetAsync_AfterExpiration_ReturnsNoValue()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider));
        await cache.SetAsync("key", "value", TimeSpan.FromMinutes(5));
        Assert.True((await cache.GetAsync<string>("key")).HasValue);
        timeProvider.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1));

        // Act
        var result = await cache.GetAsync<string>("key");

        // Assert
        Assert.False(result.HasValue);
    }
}
```

Advancing fake time drives timers registered with that provider, but does not wait for unrelated background work. Await the operation being tested, or await an `ItemExpired` notification when testing expiration events. A real-time `WaitAsync` timeout can bound that wait to prevent a hung test; it should not be used to simulate expiration.

Keep provider integration tests alongside in-memory tests. A shared interface does not guarantee identical serialization, expiration, or concurrency behavior in every implementation. See Microsoft's [FakeTimeProvider testing guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/timeprovider-testing).

## Best Practices

### 1. Use Meaningful Key Patterns

```csharp
// ✅ Good: Clear, hierarchical, identifiable
"user:123:profile"
"tenant:abc:settings"
"api:rate-limit:192.168.1.1"

// ❌ Bad: Ambiguous, no structure
"data"
"123"
"cache_item"
```

### 2. Set Appropriate Expiration

```csharp
// Session data - short expiration
await cache.SetAsync("session:xyz", data, TimeSpan.FromMinutes(30));

// Reference data - longer expiration
await cache.SetAsync("config:app", config, TimeSpan.FromHours(24));

// Computed data - based on freshness needs
await cache.SetAsync("report:daily", report, TimeSpan.FromHours(1));
```

### 3. Handle Cache Misses Gracefully

```csharp
var cached = await cache.GetAsync<User>("user:123");
if (!cached.HasValue)
{
    // Handle miss - load from source
    return await LoadFromDatabaseAsync(123);
}
// cached.Value is the User, cached.IsNull is true if explicitly cached as null
```

### 4. Use Scoped Caches for Isolation

```csharp
// Per-tenant isolation
var tenantCache = new ScopedCacheClient(cache, $"tenant:{tenantId}");

// Per-feature isolation
var featureCache = new ScopedCacheClient(cache, "feature:recommendations");
```

### 5. Consider Hybrid for High-Read Scenarios

If you're doing many reads of the same data across instances, `HybridCacheClient` can dramatically reduce latency and Redis load.

## Next Steps

- [Queues](./queues) - Message queuing for background processing
- [Locks](./locks) - Distributed locking with cache-based implementation
- [Redis Implementation](./implementations/redis) - Production Redis setup
- [Serialization](./serialization) - Serializer configuration and performance
