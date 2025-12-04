# Caching

Caching allows you to store and access data lightning fast, saving expensive operations to create or get data. Foundatio provides multiple cache implementations through the `ICacheClient` interface.

## The ICacheClient Interface

```csharp
public interface ICacheClient : IDisposable
{
    Task<bool> RemoveAsync(string key);
    Task<bool> RemoveIfEqualAsync<T>(string key, T expected);
    Task<int> RemoveAllAsync(IEnumerable<string> keys = null);
    Task<int> RemoveByPrefixAsync(string prefix);
    Task<CacheValue<T>> GetAsync<T>(string key);
    Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys);
    Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null);
    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null);
    Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null);
    Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null);
    Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null);
    Task<double> IncrementAsync(string key, double amount, TimeSpan? expiresIn = null);
    Task<long> IncrementAsync(string key, long amount, TimeSpan? expiresIn = null);
    Task<bool> ExistsAsync(string key);
    Task<TimeSpan?> GetExpirationAsync(string key);
    Task SetExpirationAsync(string key, TimeSpan expiresIn);
    Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null);
    Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null);
    Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null);
    Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null);
    Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null);
    Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null);
    Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100);
}
```

## Implementations

### InMemoryCacheClient

An in-memory cache implementation valid for the lifetime of the process:

```csharp
using Foundatio.Caching;

var cache = new InMemoryCacheClient();

// Basic operations
await cache.SetAsync("key", "value");
var result = await cache.GetAsync<string>("key");

// With expiration
await cache.SetAsync("session", sessionData, TimeSpan.FromMinutes(30));
```

#### MaxItems Configuration

Limit the number of cached items (LRU eviction):

```csharp
var cache = new InMemoryCacheClient(o => o.MaxItems = 250);

// Only keeps the last 250 items accessed
// Useful for caching resolved data like geo-ip lookups
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

### List Operations

Store and manage lists:

```csharp
// Add to a list
await cache.ListAddAsync("user:123:recent-searches", new[] { "query1" });

// Get paginated list
var searches = await cache.GetListAsync<string>(
    "user:123:recent-searches",
    page: 0,
    pageSize: 10
);

// Remove from list
await cache.ListRemoveAsync("user:123:recent-searches", new[] { "query1" });
```

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
