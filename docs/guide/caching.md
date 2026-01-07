# Caching

Caching allows you to store and access data lightning fast, saving expensive operations to create or get data. Foundatio provides multiple cache implementations through the `ICacheClient` interface.

## The ICacheClient Interface

```csharp
public interface ICacheClient : IDisposable
{
    // Key operations
    Task<bool> RemoveAsync(string key);
    Task<bool> RemoveIfEqualAsync<T>(string key, T expected);
    Task<int> RemoveAllAsync(IEnumerable<string> keys = null);
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
    Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null);
    Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null);
    Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100);
}
```

## Expiration (TTL) Behavior

Many cache methods accept an optional `expiresIn` parameter that controls the TTL (Time-To-Live) of cached items. Understanding its behavior is critical for correct cache usage.

### Quick Reference

| `expiresIn` Value | Behavior |
|-------------------|----------|
| `null` | Entry will not expire. **Removes any existing TTL** on the key. |
| Positive `TimeSpan` | Entry expires after the specified duration from now. |
| Zero or negative | **Treated as already expired.** Key is removed, operation returns failure value. |
| `TimeSpan.MaxValue` | Entry will not expire (equivalent to `null`). |

### TTL Behavior by Method

Different methods handle the `expiresIn` parameter slightly differently. The table below shows exactly what happens for each method:

| Method | `null` expiresIn | Positive expiresIn | Zero/Negative | Return on Failure |
|--------|------------------|-------------------|---------------|-------------------|
| `SetAsync` | No TTL (removes existing) | Sets TTL | Removes key | `false` |
| `AddAsync` | No TTL | Sets TTL | Removes key | `false` |
| `SetAllAsync` | No TTL (removes existing) | Sets TTL | Removes all keys | `0` |
| `ReplaceAsync` | No TTL (removes existing) | Sets TTL | Removes key | `false` |
| `ReplaceIfEqualAsync` | No TTL (removes existing) | Sets TTL | Removes key | `false` |
| `IncrementAsync` | **Preserves existing TTL** | Sets/updates TTL | Removes key | `0` |
| `SetIfHigherAsync` | No TTL (removes existing)* | Sets TTL* | Removes key | `0` |
| `SetIfLowerAsync` | No TTL (removes existing)* | Sets TTL* | Removes key | `0` |
| `ListAddAsync` | No TTL | Sets TTL | Removes key | `0` |
| `ListRemoveAsync` | Preserves existing TTL | Sets TTL | Removes key | `0` |

\* **Conditional operations**: `SetIfHigherAsync` and `SetIfLowerAsync` only update TTL when the condition is met. If the value is not higher/lower, the entire operation is a no-op (including expiration).

::: tip Key Difference: IncrementAsync
`IncrementAsync` is unique: passing `null` **preserves** any existing TTL rather than removing it. This is intentional for use cases like rate limiting, where you want to increment a counter without resetting its expiration window.

```csharp
// Set counter with 1-hour window
await cache.SetAsync("rate:user:123", 0, TimeSpan.FromHours(1));

// Increment without changing the TTL
await cache.IncrementAsync("rate:user:123", 1, null); // TTL unchanged!

// Increment AND reset TTL to 1 hour from now
await cache.IncrementAsync("rate:user:123", 1, TimeSpan.FromHours(1));
```

:::

::: info Integer vs Floating-Point Increments
`IncrementAsync` supports both integer (`long`) and floating-point (`double`) amounts. Both overloads work correctly with expiration:

```csharp
// Integer increments
await cache.IncrementAsync("counter", 1L, TimeSpan.FromHours(1));    // long overload
await cache.IncrementAsync("counter", 5L, TimeSpan.FromHours(1));

// Floating-point increments
await cache.IncrementAsync("score", 1.5, TimeSpan.FromHours(1));     // double overload
await cache.IncrementAsync("score", 2.25, TimeSpan.FromHours(1));    // Total: 3.75

// Mixed increments work correctly
await cache.IncrementAsync("mixed", 1, TimeSpan.FromHours(1));       // 1
await cache.IncrementAsync("mixed", 1.5, TimeSpan.FromHours(1));     // 2.5
await cache.IncrementAsync("mixed", 2, TimeSpan.FromHours(1));       // 4.5
```

For Redis implementations, integer amounts (including `2.0` where the fractional part is zero) use the more efficient `INCRBY` command, while fractional amounts use `INCRBYFLOAT`.
:::

### Detailed Examples

```csharp
// === Basic Set Operations ===

// No expiration - item lives until explicitly removed
await cache.SetAsync("permanent-key", value);           // null is default
await cache.SetAsync("also-permanent", value, null);    // explicit null

// Expires in 30 minutes
await cache.SetAsync("session", data, TimeSpan.FromMinutes(30));

// Never expires (equivalent to null)
await cache.SetAsync("config", settings, TimeSpan.MaxValue);

// Zero/negative = expired, key removed, returns false
var success = await cache.SetAsync("invalid", value, TimeSpan.Zero);        // false
var alsoFails = await cache.SetAsync("invalid", value, TimeSpan.FromSeconds(-1)); // false


// === Increment Operations (TTL Preservation) ===

// Create counter with TTL
await cache.SetAsync("counter", 0, TimeSpan.FromMinutes(5));

// Increment preserves TTL when null
await cache.IncrementAsync("counter", 1, null);  // TTL still ~5 min

// Increment with explicit TTL resets it
await cache.IncrementAsync("counter", 1, TimeSpan.FromMinutes(10)); // TTL now 10 min

// Zero/negative removes key, returns 0
var result = await cache.IncrementAsync("counter", 5, TimeSpan.Zero); // 0


// === SetIfHigher/SetIfLower (TTL Removal) ===

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
- Use `TimeSpan.MaxValue` only when you explicitly need permanent storage
- Monitor your Redis memory usage and eviction metrics

**Further Reading:**

- [Azure Managed Cache for Redis eviction policies](https://docs.microsoft.com/en-us/azure/azure-cache-for-redis/cache-configure#memory-policies)
- [Redis eviction policies documentation](https://redis.io/docs/reference/eviction/)
:::

## Implementations

### InMemoryCacheClient

An in-memory cache implementation valid for the lifetime of the process. See the [In-Memory Implementation Guide](./implementations/in-memory) for detailed configuration options including memory-based eviction.

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

### HybridCacheClient

Combines local in-memory caching with a distributed cache for maximum performance:

```csharp
using Foundatio.Caching;

var hybridCache = new HybridCacheClient(
    distributedCache: redisCacheClient,
    messageBus: redisMessageBus
);

// First access: fetches from Redis, caches locally
var user = await hybridCache.GetAsync<User>("user:123");

// Subsequent access: returns from local cache (no network call)
var sameUser = await hybridCache.GetAsync<User>("user:123");
```

**How it works:**

1. Reads check local cache first
2. On miss, reads from distributed cache and caches locally
3. Writes go to distributed cache and publish invalidation message
4. All instances receive invalidation and clear local cache

**Benefits:**

- **Huge performance gains**: Skip serialization and network calls
- **Consistency**: Message bus keeps all instances in sync
- **Automatic**: No manual cache invalidation logic

### ScopedCacheClient

Prefix all cache keys for easy namespacing:

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

```csharp
// dotnet add package Foundatio.Redis

using Foundatio.Redis.Cache;
using StackExchange.Redis;

var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var cache = new RedisCacheClient(o => o.ConnectionMultiplexer = redis);

await cache.SetAsync("user:123", user, TimeSpan.FromHours(1));
```

### RedisHybridCacheClient

Combines `RedisCacheClient` with `HybridCacheClient`:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var hybridCache = new RedisHybridCacheClient(o => {
    o.ConnectionMultiplexer = redis;
    o.LocalCacheMaxItems = 1000;
});
```

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

// Value is NOT higher - returns 0 (no change)
diff = await cache.SetIfHigherAsync("max-users", 120); // Returns 0

// To get the actual current value after the operation:
var currentMax = (await cache.GetAsync<double>("max-users")).Value; // 150
```

::: warning Conditional Expiration Behavior
`SetIfHigherAsync` and `SetIfLowerAsync` only update the expiration **when the condition is met**. If the value is not higher/lower, the operation is a complete no-op—including the expiration.

```csharp
// Set with 1-hour TTL
await cache.SetIfHigherAsync("max-users", 100, TimeSpan.FromHours(1));

// Try to set lower value with 2-hour TTL
await cache.SetIfHigherAsync("max-users", 50, TimeSpan.FromHours(2));
// TTL is STILL 1 hour! The condition failed, so nothing changed.

// Set higher value with 2-hour TTL
await cache.SetIfHigherAsync("max-users", 200, TimeSpan.FromHours(2));
// TTL is now 2 hours (condition was met)
```

This is intentional—the semantic is "set IF higher/lower", so a failed condition means the entire operation is skipped.
:::

### List Operations

Foundatio lists support **per-value expiration**, where each item in the list can have its own independent TTL. This is different from standard cache keys where expiration applies to the entire key.

#### Why Per-Value Expiration?

Per-value expiration prevents unbounded list growth. Consider tracking recently deleted items:

```csharp
// Without per-value expiration (sliding expiration problem):
// Adding ANY item resets the entire list's TTL, causing indefinite growth
await cache.ListAddAsync("deleted-items", [itemId], TimeSpan.FromDays(7));
// After months: list has 100,000+ items because TTL keeps resetting!

// With per-value expiration (Foundatio's approach):
// Each item expires independently after 7 days
await cache.ListAddAsync("deleted-items", [itemId], TimeSpan.FromDays(7));
// List stays bounded - old items expire even as new ones are added
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

// Get paginated list (expired items are automatically filtered)
var searches = await cache.GetListAsync<string>(
    "user:123:recent-searches",
    page: 0,
    pageSize: 10
);

// Remove specific items from list
await cache.ListRemoveAsync("user:123:recent-searches", new[] { "query1" });
```

#### List Expiration Behavior

| `expiresIn` Value | Behavior |
|-------------------|----------|
| `null` | Values will not expire. Key expiration is set to max of all item expirations. |
| Positive `TimeSpan` | Each value expires independently after this duration. |
| Zero or negative | The specified values are removed from the list (if present), returns 0. |

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
    new InMemoryCacheClient(o => o.MaxItems = 1000));

// Redis (production)
services.AddSingleton<ICacheClient>(sp =>
    new RedisCacheClient(o => o.ConnectionMultiplexer = redis));
```

### Hybrid with DI

```csharp
services.AddSingleton<ICacheClient>(sp =>
{
    var redis = sp.GetRequiredService<IConnectionMultiplexer>();
    return new HybridCacheClient(
        new RedisCacheClient(o => o.ConnectionMultiplexer = redis),
        sp.GetRequiredService<IMessageBus>()
    );
});
```

### Named Caches

Use different caches for different purposes:

```csharp
services.AddKeyedSingleton<ICacheClient>("session",
    new InMemoryCacheClient(o => o.MaxItems = 10000));
services.AddKeyedSingleton<ICacheClient>("geo",
    new InMemoryCacheClient(o => o.MaxItems = 250));
```

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
