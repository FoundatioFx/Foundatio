# Locks

Locks ensure a resource is only accessed by one consumer at any given time. Foundatio provides distributed locking implementations through the `ILockProvider` interface.

## The ILockProvider Interface

```csharp
public interface ILockProvider
{
    Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null,
                              bool releaseOnDispose = true,
                              CancellationToken cancellationToken = default);
    Task<bool> IsLockedAsync(string resource);
    Task ReleaseAsync(string resource, string lockId);
    Task ReleaseAsync(string resource);
    Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null);
}

public interface ILock : IAsyncDisposable
{
    Task RenewAsync(TimeSpan? timeUntilExpires = null);
    Task ReleaseAsync();
    string LockId { get; }
    string Resource { get; }
    DateTime AcquiredTimeUtc { get; }
    TimeSpan TimeWaitedForLock { get; }
    int RenewalCount { get; }
}
```

## Implementations

### CacheLockProvider

Uses a cache client and message bus for distributed locking:

```csharp
using Foundatio.Lock;
using Foundatio.Caching;
using Foundatio.Messaging;

var cache = new InMemoryCacheClient();
var messageBus = new InMemoryMessageBus();
var locker = new CacheLockProvider(cache, messageBus);

await using var lck = await locker.AcquireAsync("my-resource");
if (lck != null)
{
    // Exclusive access to resource
    await DoExclusiveWorkAsync();
}
```

With Redis for production:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
var cache = new RedisCacheClient(o => o.ConnectionMultiplexer = redis);
var messageBus = new RedisMessageBus(o => o.Subscriber = redis.GetSubscriber());
var locker = new CacheLockProvider(cache, messageBus);
```

### ThrottlingLockProvider

Limits the number of operations within a time period:

```csharp
using Foundatio.Lock;

var throttledLocker = new ThrottlingLockProvider(
    cache,
    maxHits: 10,           // Maximum locks allowed
    period: TimeSpan.FromMinutes(1)  // Per time period
);

// Only allows 10 operations per minute across all instances
var lck = await throttledLocker.AcquireAsync("api-rate-limit");
if (lck != null)
{
    await CallExternalApiAsync();
    await lck.ReleaseAsync();
}
else
{
    // Rate limited
    throw new TooManyRequestsException();
}
```

### ScopedLockProvider

Prefixes all lock keys with a scope:

```csharp
using Foundatio.Lock;

var baseLock = new CacheLockProvider(cache, messageBus);
var tenantLock = new ScopedLockProvider(baseLock, "tenant:abc");

// Lock key becomes: "tenant:abc:resource-1"
await using var lck = await tenantLock.AcquireAsync("resource-1");
```

## Basic Usage

### Acquire and Release

```csharp
var locker = new CacheLockProvider(cache, messageBus);

// Acquire lock
var lck = await locker.AcquireAsync("my-resource");

if (lck != null)
{
    try
    {
        // Do exclusive work
        await ProcessAsync();
    }
    finally
    {
        // Always release
        await lck.ReleaseAsync();
    }
}
```

### Using Dispose Pattern

The recommended pattern uses `await using` for automatic release:

```csharp
await using var lck = await locker.AcquireAsync("my-resource");
if (lck != null)
{
    // Lock is automatically released when scope ends
    await DoExclusiveWorkAsync();
}
```

### Non-Blocking Acquire

Check if lock was acquired:

```csharp
await using var lck = await locker.AcquireAsync("my-resource");
if (lck == null)
{
    // Resource is locked by another process
    return;
}

// Got the lock
await DoWorkAsync();
```

### Blocking Acquire with Timeout

Wait for lock with cancellation:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

try
{
    await using var lck = await locker.AcquireAsync(
        "my-resource",
        cancellationToken: cts.Token
    );

    if (lck != null)
    {
        await DoWorkAsync();
    }
}
catch (OperationCanceledException)
{
    // Timeout waiting for lock
    _logger.LogWarning("Timed out waiting for lock");
}
```

## Lock Expiration

### Setting Expiration

Locks expire automatically to prevent deadlocks:

```csharp
// Lock expires after 5 minutes
await using var lck = await locker.AcquireAsync(
    "my-resource",
    timeUntilExpires: TimeSpan.FromMinutes(5)
);
```

### Renewing Locks

For long-running operations, renew the lock:

```csharp
await using var lck = await locker.AcquireAsync(
    "my-resource",
    timeUntilExpires: TimeSpan.FromMinutes(1)
);

if (lck != null)
{
    // Do some work
    await DoPartOneAsync();

    // Renew lock for more time
    await lck.RenewAsync(TimeSpan.FromMinutes(1));

    // Continue work
    await DoPartTwoAsync();
}
```

### Automatic Renewal

For very long operations, set up automatic renewal:

```csharp
await using var lck = await locker.AcquireAsync("my-resource");
if (lck == null) return;

using var cts = new CancellationTokenSource();

// Start renewal task
var renewTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
        await lck.RenewAsync(TimeSpan.FromMinutes(1));
    }
});

try
{
    await VeryLongRunningOperationAsync();
}
finally
{
    cts.Cancel();
}
```

## Common Patterns

### Singleton Processing

Ensure only one instance processes a resource:

```csharp
public async Task ProcessOrderAsync(int orderId)
{
    await using var lck = await _locker.AcquireAsync($"order:{orderId}");

    if (lck == null)
    {
        _logger.LogInformation("Order {OrderId} is being processed elsewhere", orderId);
        return;
    }

    // Only one instance processes this order
    await DoProcessingAsync(orderId);
}
```

### Leader Election

Elect a single instance for a job:

```csharp
public async Task RunAsLeaderAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await using var lck = await _locker.AcquireAsync("leader:job-runner");

        if (lck != null)
        {
            _logger.LogInformation("This instance is now the leader");

            // Keep renewing while leading
            while (!ct.IsCancellationRequested)
            {
                await DoLeaderWorkAsync();
                await lck.RenewAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        else
        {
            // Not leader, wait and try again
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
```

### Preventing Duplicate Operations

```csharp
public async Task<Order> CreateOrderAsync(CreateOrderRequest request)
{
    // Prevent duplicate orders for same customer
    await using var lck = await _locker.AcquireAsync(
        $"create-order:{request.CustomerId}",
        timeUntilExpires: TimeSpan.FromSeconds(30)
    );

    if (lck == null)
    {
        throw new ConcurrencyException("Another order is being created");
    }

    // Check for recent duplicates
    var recentOrder = await _db.GetRecentOrderAsync(request.CustomerId);
    if (recentOrder != null && recentOrder.IsSimilar(request))
    {
        throw new DuplicateOrderException();
    }

    return await _db.CreateOrderAsync(request);
}
```

### Using TryUsingAsync Extension

Simplified pattern for lock-protected operations:

```csharp
var success = await locker.TryUsingAsync(
    "my-resource",
    async ct =>
    {
        await DoExclusiveWorkAsync(ct);
    },
    timeUntilExpires: TimeSpan.FromMinutes(5),
    cancellationToken
);

if (!success)
{
    _logger.LogWarning("Could not acquire lock");
}
```

## Rate Limiting with ThrottlingLockProvider

### API Rate Limiting

```csharp
public class RateLimitedApiClient
{
    private readonly ThrottlingLockProvider _throttler;
    private readonly HttpClient _client;

    public RateLimitedApiClient(ICacheClient cache, HttpClient client)
    {
        _throttler = new ThrottlingLockProvider(
            cache,
            maxHits: 100,                    // 100 requests
            period: TimeSpan.FromMinutes(1)  // per minute
        );
        _client = client;
    }

    public async Task<T> GetAsync<T>(string endpoint)
    {
        await using var lck = await _throttler.AcquireAsync("external-api");

        if (lck == null)
        {
            throw new RateLimitExceededException();
        }

        var response = await _client.GetAsync(endpoint);
        return await response.Content.ReadFromJsonAsync<T>();
    }
}
```

### Per-User Rate Limiting

```csharp
public async Task<IActionResult> ProcessRequest(string userId)
{
    // 10 requests per minute per user
    await using var lck = await _throttler.AcquireAsync($"user:{userId}:api");

    if (lck == null)
    {
        return StatusCode(429, "Too many requests");
    }

    return Ok(await ProcessAsync());
}
```

## Dependency Injection

### Basic Registration

```csharp
services.AddSingleton<ICacheClient, InMemoryCacheClient>();
services.AddSingleton<IMessageBus, InMemoryMessageBus>();

services.AddSingleton<ILockProvider>(sp =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetRequiredService<IMessageBus>()
    )
);
```

### With Redis

```csharp
services.AddSingleton<IConnectionMultiplexer>(
    await ConnectionMultiplexer.ConnectAsync("localhost:6379")
);

services.AddSingleton<ICacheClient>(sp =>
    new RedisCacheClient(o =>
        o.ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>()
    )
);

services.AddSingleton<IMessageBus>(sp =>
    new RedisMessageBus(o =>
        o.Subscriber = sp.GetRequiredService<IConnectionMultiplexer>().GetSubscriber()
    )
);

services.AddSingleton<ILockProvider>(sp =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetRequiredService<IMessageBus>()
    )
);
```

### Multiple Lock Providers

```csharp
// General-purpose locking
services.AddKeyedSingleton<ILockProvider>("general", (sp, _) =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetRequiredService<IMessageBus>()
    )
);

// Rate limiting
services.AddKeyedSingleton<ILockProvider>("throttle", (sp, _) =>
    new ThrottlingLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        maxHits: 100,
        period: TimeSpan.FromMinutes(1)
    )
);
```

## Best Practices

### 1. Always Handle Null Lock

**Critical:** `AcquireAsync` returns `null` when the lock cannot be acquired. You **must** handle this case.

```csharp
// ✅ Good: Check for null and handle appropriately
await using var lck = await locker.AcquireAsync("resource");

if (lck is null)
{
    _logger.LogWarning("Could not acquire lock for resource");
    return; // or throw, or retry
}

await DoWork();

// ✅ Good: Throw when lock is required
await using var lck = await locker.AcquireAsync("resource");

if (lck is null)
    throw new InvalidOperationException("Failed to acquire lock on 'resource'");

await DoWork();

// ❌ Bad: Assume lock acquired
await using var lck = await locker.AcquireAsync("resource");
await DoWork(); // May not have the lock!
```

### 2. Common Null-Handling Patterns

**Pattern: Throw Exception**

```csharp
public async Task ProcessOrderAsync(int orderId)
{
    await using var lck = await _locker.AcquireAsync($"order:{orderId}");

    if (lck is null)
        throw new ConcurrencyException($"Order {orderId} is being processed by another worker");

    await DoProcessingAsync(orderId);
}
```

**Pattern: Skip Processing**

```csharp
public async Task TryProcessOrderAsync(int orderId)
{
    await using var lck = await _locker.AcquireAsync($"order:{orderId}");

    if (lck is null)
    {
        _logger.LogDebug("Order {OrderId} is locked, skipping", orderId);
        return;
    }

    await DoProcessingAsync(orderId);
}
```

**Pattern: Retry with Timeout**

```csharp
public async Task ProcessOrderWithRetryAsync(int orderId, CancellationToken ct)
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

    await using var lck = await _locker.AcquireAsync(
        $"order:{orderId}",
        cancellationToken: timeoutCts.Token
    );

    if (lck is null)
        throw new TimeoutException($"Timed out waiting for lock on order {orderId}");

    await DoProcessingAsync(orderId);
}
```

**Pattern: Return Result**

```csharp
public async Task<LockResult<T>> TryWithLockAsync<T>(string resource, Func<Task<T>> work)
{
    await using var lck = await _locker.AcquireAsync(resource);

    if (lck is null)
        return LockResult<T>.NotAcquired();

    var result = await work();
    return LockResult<T>.Success(result);
}
```

### 3. Use Meaningful Lock Names

```csharp
// ✅ Good: Descriptive, hierarchical
await locker.AcquireAsync($"order:process:{orderId}");
await locker.AcquireAsync($"user:{userId}:balance:update");

// ❌ Bad: Generic, ambiguous
await locker.AcquireAsync("lock1");
await locker.AcquireAsync("resource");
```

### 4. Set Appropriate Expiration

```csharp
// Match expiration to expected operation duration + buffer
await locker.AcquireAsync("quick-op", TimeSpan.FromSeconds(30));   // 10s operation + buffer
await locker.AcquireAsync("long-op", TimeSpan.FromMinutes(10));    // 5min operation + buffer
```

::: tip Default Expiration
If no expiration is specified, `CacheLockProvider` defaults to 20 minutes. Always set an explicit expiration based on your expected operation duration.
:::

### 5. Prefer `await using` Pattern

Locks implement `IAsyncDisposable`. Using `await using` ensures the lock is released even if an exception occurs:

```csharp
// ✅ Good: Automatic release on dispose
await using var lck = await locker.AcquireAsync("resource");

if (lck is null)
    return;

await DoWork();
// Lock is automatically released when scope ends

// ✅ Good: Manual release when needed
var lck = await locker.AcquireAsync("resource", releaseOnDispose: false);

if (lck is null)
    return;

try
{
    await DoWork();
}
finally
{
    await lck.ReleaseAsync();
}

// ❌ Bad: Not using dispose pattern
var lck = await locker.AcquireAsync("resource");

if (lck is null)
    return;

await DoWork();
// Lock may not be released if DoWork throws!
```

### 6. Use Scoped Locks for Multi-Tenant

```csharp
var tenantLock = new ScopedLockProvider(baseLock, $"tenant:{tenantId}");
// All locks are isolated per tenant
```

## Lock Information

Access lock metadata:

```csharp
await using var lck = await locker.AcquireAsync("resource");
if (lck != null)
{
    Console.WriteLine($"Lock ID: {lck.LockId}");
    Console.WriteLine($"Resource: {lck.Resource}");
    Console.WriteLine($"Acquired: {lck.AcquiredTimeUtc}");
    Console.WriteLine($"Wait time: {lck.TimeWaitedForLock}");
    Console.WriteLine($"Renewals: {lck.RenewalCount}");
}
```

## Next Steps

- [Caching](./caching) - Cache implementations used by locks
- [Messaging](./messaging) - Message bus used for lock coordination
- [Jobs](./jobs) - Background jobs with distributed locking
- [Resilience](./resilience) - Retry policies for lock acquisition
- [Serialization](./serialization) - Serializer configuration and performance