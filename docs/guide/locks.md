# Locks

Locks ensure a resource is only accessed by one consumer at any given time. Foundatio provides distributed locking implementations through the `ILockProvider` interface.

## The ILockProvider Interface

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Lock/ILockProvider.cs)

```csharp
public interface ILockProvider
{
    Task<ILock?> TryAcquireAsync(string resource, TimeSpan? timeUntilExpires = null,
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

### Choosing Between `AcquireAsync` and `TryAcquireAsync`

`ILockProvider` exposes two acquisition shapes:

| API | Returns | Use when |
| --- | --- | --- |
| `TryAcquireAsync` | `Task<ILock?>` — `null` on failure | Lock unavailability is a normal control-flow outcome (best-effort dedupe, opportunistic work) |
| `AcquireAsync` | `Task<ILock>` — throws `LockAcquisitionTimeoutException` on failure | The work cannot safely run without the lock — failure is genuinely exceptional |

The throwing `AcquireAsync` is provided as an extension method on `ILockProvider`. Pick whichever one matches the caller's intent — they share the same underlying acquisition logic.

## Implementations

### CacheLockProvider

Uses a cache client and message bus for distributed locking:

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Lock/CacheLockProvider.cs)

```csharp
using Foundatio.Lock;
using Foundatio.Caching;
using Foundatio.Messaging;

var cache = new InMemoryCacheClient();
var messageBus = new InMemoryMessageBus();
var locker = new CacheLockProvider(cache, messageBus);

await using var lck = await locker.TryAcquireAsync("my-resource");
if (lck is not null)
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

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Lock/ThrottlingLockProvider.cs)

```csharp
using Foundatio.Lock;

var throttledLocker = new ThrottlingLockProvider(
    cache,
    maxHits: 10,           // Maximum locks allowed
    period: TimeSpan.FromMinutes(1)  // Per time period
);

// Only allows 10 operations per minute across all instances
var lck = await throttledLocker.TryAcquireAsync("api-rate-limit");
if (lck is not null)
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

[View source](https://github.com/FoundatioFx/Foundatio/blob/main/src/Foundatio/Lock/ScopedLockProvider.cs)

```csharp
using Foundatio.Lock;

var baseLock = new CacheLockProvider(cache, messageBus);
var tenantLock = new ScopedLockProvider(baseLock, "tenant:abc");

// Lock key becomes: "tenant:abc:resource-1"
await using var lck = await tenantLock.TryAcquireAsync("resource-1");
```

## Basic Usage

### Acquire and Release

```csharp
var locker = new CacheLockProvider(cache, messageBus);

// Best-effort acquire — null when held elsewhere
var lck = await locker.TryAcquireAsync("my-resource");

if (lck is not null)
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
await using var lck = await locker.TryAcquireAsync("my-resource");
if (lck is not null)
{
    // Lock is automatically released when scope ends
    await DoExclusiveWorkAsync();
}
```

### Non-Blocking Acquire

Try to acquire without waiting:

```csharp
await using var lck = await locker.TryAcquireAsync("my-resource");
if (lck is null)
{
    // Resource is locked by another process
    return;
}

// Got the lock
await DoWorkAsync();
```

### Blocking Acquire with Timeout

Wait up to a timeout, throwing on failure:

```csharp
try
{
    await using var lck = await locker.AcquireAsync(
        "my-resource",
        acquireTimeout: TimeSpan.FromSeconds(30));

    await DoWorkAsync();
}
catch (LockAcquisitionTimeoutException)
{
    // Lock could not be acquired before the timeout elapsed.
    _logger.LogWarning("Timed out waiting for lock");
}
```

If the failure is expected control flow, prefer `TryAcquireAsync` so you don't pay for an exception:

```csharp
await using var lck = await locker.TryAcquireAsync(
    "my-resource",
    acquireTimeout: TimeSpan.FromSeconds(30));

if (lck is null)
{
    _logger.LogWarning("Could not acquire lock");
    return;
}

await DoWorkAsync();
```

## Lock Expiration

### Setting Expiration

Locks expire automatically to prevent deadlocks:

```csharp
// Lock expires after 5 minutes
await using var lck = await locker.TryAcquireAsync(
    "my-resource",
    timeUntilExpires: TimeSpan.FromMinutes(5)
);
```

### Renewing Locks

For long-running operations, renew the lock:

```csharp
await using var lck = await locker.TryAcquireAsync(
    "my-resource",
    timeUntilExpires: TimeSpan.FromMinutes(1)
);

if (lck is not null)
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
await using var lck = await locker.TryAcquireAsync("my-resource");
if (lck is null) return;

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
    await using var lck = await _locker.TryAcquireAsync($"order:{orderId}");

    if (lck is null)
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
        await using var lck = await _locker.TryAcquireAsync("leader:job-runner");

        if (lck is not null)
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
    // Prevent duplicate orders for same customer; throw if we can't lock.
    await using var lck = await _locker.AcquireAsync(
        $"create-order:{request.CustomerId}",
        timeUntilExpires: TimeSpan.FromSeconds(30),
        acquireTimeout: TimeSpan.FromSeconds(5)
    );

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
        await using var lck = await _throttler.TryAcquireAsync("external-api");

        if (lck is null)
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
    await using var lck = await _throttler.TryAcquireAsync($"user:{userId}:api");

    if (lck is null)
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

### 1. Pick the API That Matches Your Intent

If failure to acquire is **normal control flow** (best-effort dedupe, opportunistic work), call `TryAcquireAsync` and check for `null`. If failure is **genuinely exceptional** (the work cannot run safely without the lock), call `AcquireAsync` and let it throw.

```csharp
// ✅ Best-effort: skip work when lock is held elsewhere
await using var lck = await locker.TryAcquireAsync("resource");
if (lck is null)
    return;

await DoWork();

// ✅ Required lock: throw if we can't get it
await using var lck = await locker.AcquireAsync("resource",
    acquireTimeout: TimeSpan.FromSeconds(5));
await DoWork();
// ↑ If acquisition fails, LockAcquisitionTimeoutException propagates.
```

::: warning
Do not catch `LockAcquisitionTimeoutException` to convert "couldn't acquire" into a normal control-flow path — that is exactly the case `TryAcquireAsync` is designed for. Use the right method up front.
:::

### 2. Common Patterns

**Pattern: Skip Processing (best-effort)**

```csharp
public async Task TryProcessOrderAsync(int orderId)
{
    await using var lck = await _locker.TryAcquireAsync($"order:{orderId}");

    if (lck is null)
    {
        _logger.LogDebug("Order {OrderId} is locked, skipping", orderId);
        return;
    }

    await DoProcessingAsync(orderId);
}
```

**Pattern: Required Lock (throws)**

```csharp
public async Task ProcessOrderAsync(int orderId)
{
    // Throws LockAcquisitionTimeoutException if the lock can't be acquired.
    await using var lck = await _locker.AcquireAsync(
        $"order:{orderId}",
        acquireTimeout: TimeSpan.FromSeconds(30));

    await DoProcessingAsync(orderId);
}
```

**Pattern: Wait with Cancellation**

```csharp
public async Task ProcessOrderAsync(int orderId, CancellationToken ct)
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

    await using var lck = await _locker.AcquireAsync(
        $"order:{orderId}",
        cancellationToken: timeoutCts.Token);

    await DoProcessingAsync(orderId);
}
```

**Pattern: Return Result (best-effort)**

```csharp
public async Task<LockResult<T>> TryWithLockAsync<T>(string resource, Func<Task<T>> work)
{
    await using var lck = await _locker.TryAcquireAsync(resource);

    if (lck is null)
        return LockResult<T>.NotAcquired();

    var result = await work();
    return LockResult<T>.Success(result);
}
```

### 3. Use Meaningful Lock Names

```csharp
// ✅ Good: Descriptive, hierarchical
await locker.TryAcquireAsync($"order:process:{orderId}");
await locker.TryAcquireAsync($"user:{userId}:balance:update");

// ❌ Bad: Generic, ambiguous
await locker.TryAcquireAsync("lock1");
await locker.TryAcquireAsync("resource");
```

### 4. Set Appropriate Expiration

```csharp
// Match expiration to expected operation duration + buffer
await locker.TryAcquireAsync("quick-op", TimeSpan.FromSeconds(30));   // 10s operation + buffer
await locker.TryAcquireAsync("long-op", TimeSpan.FromMinutes(10));    // 5min operation + buffer
```

::: tip Default Expiration
If no expiration is specified, `CacheLockProvider` defaults to 20 minutes. Always set an explicit expiration based on your expected operation duration.
:::

### 5. Prefer `await using` Pattern

Locks implement `IAsyncDisposable`. Using `await using` ensures the lock is released even if an exception occurs:

```csharp
// ✅ Good: Automatic release on dispose
await using var lck = await locker.TryAcquireAsync("resource");

if (lck is null)
    return;

await DoWork();
// Lock is automatically released when scope ends

// ✅ Good: Manual release when needed
var lck = await locker.TryAcquireAsync("resource", releaseOnDispose: false);

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
var lck = await locker.TryAcquireAsync("resource");

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
await using var lck = await locker.TryAcquireAsync("resource");
if (lck is not null)
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