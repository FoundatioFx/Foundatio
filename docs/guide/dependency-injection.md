# Dependency Injection

Foundatio is designed to work seamlessly with Microsoft.Extensions.DependencyInjection. All abstractions are interface-based and can be easily registered and resolved.

## Basic Registration

### Manual Registration

```csharp
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Lock;
using Foundatio.Storage;
using Foundatio.Queues;

var builder = WebApplication.CreateBuilder(args);

// Core services
builder.Services.AddSingleton<ICacheClient, InMemoryCacheClient>();
builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
builder.Services.AddSingleton<IFileStorage, InMemoryFileStorage>();

// Lock provider (depends on cache and message bus)
builder.Services.AddSingleton<ILockProvider>(sp =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetRequiredService<IMessageBus>()
    )
);

// Queues
builder.Services.AddSingleton<IQueue<OrderWorkItem>>(sp =>
    new InMemoryQueue<OrderWorkItem>()
);
```

### Using Extension Methods

```csharp
using Foundatio;

builder.Services.AddFoundatio();  // Adds default in-memory implementations
```

## Service Lifetimes

### Recommended Lifetimes

| Service | Lifetime | Reason |
|---------|----------|--------|
| `ICacheClient` | Singleton | Maintains internal state/connection |
| `IMessageBus` | Singleton | Maintains subscriptions |
| `ILockProvider` | Singleton | Stateless, thread-safe |
| `IFileStorage` | Singleton | Stateless, thread-safe |
| `IQueue<T>` | Singleton | Maintains queue state |
| Jobs | Scoped | Per-execution isolation |

### Example Registration

```csharp
// Singletons for infrastructure
builder.Services.AddSingleton<ICacheClient>(sp =>
    new InMemoryCacheClient(o => o.MaxItems = 1000));

builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();

// Scoped for per-request isolation
builder.Services.AddScoped<ILockProvider>(sp =>
    new ScopedLockProvider(
        sp.GetRequiredService<CacheLockProvider>(),
        $"tenant:{GetCurrentTenantId(sp)}"
    )
);
```

## Environment-Based Configuration

### Development vs Production

```csharp
if (builder.Environment.IsDevelopment())
{
    // In-memory for development
    builder.Services.AddSingleton<ICacheClient, InMemoryCacheClient>();
    builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
    builder.Services.AddSingleton<IFileStorage, InMemoryFileStorage>();
}
else
{
    // Redis for production
    var redis = ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis")
    );

    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

    builder.Services.AddSingleton<ICacheClient>(sp =>
        new RedisCacheClient(o => o.ConnectionMultiplexer = redis));

    builder.Services.AddSingleton<IMessageBus>(sp =>
        new RedisMessageBus(o => o.Subscriber = redis.GetSubscriber()));

    builder.Services.AddSingleton<IFileStorage>(sp =>
        new AzureFileStorage(o => {
            o.ConnectionString = builder.Configuration["Azure:StorageConnectionString"];
            o.ContainerName = "files";
        }));
}
```

### Using Options Pattern

```csharp
// appsettings.json
{
  "Foundatio": {
    "Cache": {
      "Type": "Redis",
      "MaxItems": 1000
    },
    "Storage": {
      "Type": "Azure",
      "ContainerName": "files"
    }
  }
}

// Registration
builder.Services.Configure<FoundatioOptions>(
    builder.Configuration.GetSection("Foundatio"));

builder.Services.AddSingleton<ICacheClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<FoundatioOptions>>().Value;
    return options.Cache.Type switch
    {
        "Redis" => new RedisCacheClient(...),
        "InMemory" => new InMemoryCacheClient(o => o.MaxItems = options.Cache.MaxItems),
        _ => throw new InvalidOperationException()
    };
});
```

## Named/Keyed Services

### Multiple Implementations

```csharp
// Multiple caches
builder.Services.AddKeyedSingleton<ICacheClient>("session",
    sp => new InMemoryCacheClient(o => o.MaxItems = 10000));

builder.Services.AddKeyedSingleton<ICacheClient>("data",
    sp => new RedisCacheClient(o => o.ConnectionMultiplexer = redis));

// Multiple queues
builder.Services.AddKeyedSingleton<IQueue<WorkItem>>("high-priority",
    sp => new InMemoryQueue<WorkItem>());

builder.Services.AddKeyedSingleton<IQueue<WorkItem>>("low-priority",
    sp => new InMemoryQueue<WorkItem>());
```

### Injecting Keyed Services

```csharp
public class OrderService
{
    private readonly ICacheClient _sessionCache;
    private readonly ICacheClient _dataCache;

    public OrderService(
        [FromKeyedServices("session")] ICacheClient sessionCache,
        [FromKeyedServices("data")] ICacheClient dataCache)
    {
        _sessionCache = sessionCache;
        _dataCache = dataCache;
    }
}
```

## Factory Pattern

### Dynamic Resolution

```csharp
public interface ICacheClientFactory
{
    ICacheClient GetCache(string name);
}

public class CacheClientFactory : ICacheClientFactory
{
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, ICacheClient> _caches = new();

    public CacheClientFactory(IServiceProvider services)
    {
        _services = services;
    }

    public ICacheClient GetCache(string name)
    {
        return _caches.GetOrAdd(name, n =>
        {
            var baseCache = _services.GetRequiredService<ICacheClient>();
            return new ScopedCacheClient(baseCache, n);
        });
    }
}

// Registration
builder.Services.AddSingleton<ICacheClient, InMemoryCacheClient>();
builder.Services.AddSingleton<ICacheClientFactory, CacheClientFactory>();
```

## Multi-Tenant Support

### Tenant-Scoped Services

```csharp
public interface ITenantAccessor
{
    string TenantId { get; }
}

// Scoped cache per tenant
builder.Services.AddScoped<ICacheClient>(sp =>
{
    var baseCache = sp.GetRequiredService<InMemoryCacheClient>();
    var tenant = sp.GetRequiredService<ITenantAccessor>();
    return new ScopedCacheClient(baseCache, $"tenant:{tenant.TenantId}");
});

// Scoped storage per tenant
builder.Services.AddScoped<IFileStorage>(sp =>
{
    var baseStorage = sp.GetRequiredService<FolderFileStorage>();
    var tenant = sp.GetRequiredService<ITenantAccessor>();
    return new ScopedFileStorage(baseStorage, tenant.TenantId);
});

// Scoped locks per tenant
builder.Services.AddScoped<ILockProvider>(sp =>
{
    var baseLock = sp.GetRequiredService<CacheLockProvider>();
    var tenant = sp.GetRequiredService<ITenantAccessor>();
    return new ScopedLockProvider(baseLock, tenant.TenantId);
});
```

## Health Checks

### Register Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<CacheHealthCheck>("cache")
    .AddCheck<StorageHealthCheck>("storage")
    .AddCheck<QueueHealthCheck>("queue");

public class CacheHealthCheck : IHealthCheck
{
    private readonly ICacheClient _cache;

    public CacheHealthCheck(ICacheClient cache) => _cache = cache;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.SetAsync("health-check", DateTime.UtcNow);
            var result = await _cache.GetAsync<DateTime>("health-check");

            return result.HasValue
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Cache read failed");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
```

## Testing

### Test-Friendly Registration

```csharp
// In test setup
public class TestStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Always use in-memory for tests
        services.AddSingleton<ICacheClient, InMemoryCacheClient>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        services.AddSingleton<IFileStorage, InMemoryFileStorage>();
        services.AddSingleton<ILockProvider>(sp =>
            new CacheLockProvider(
                sp.GetRequiredService<ICacheClient>(),
                sp.GetRequiredService<IMessageBus>()
            )
        );
    }
}
```

### Isolated Tests

```csharp
public class OrderServiceTests
{
    private readonly ServiceProvider _services;

    public OrderServiceTests()
    {
        var services = new ServiceCollection();

        // Fresh instances for each test class
        services.AddSingleton<ICacheClient, InMemoryCacheClient>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();

        _services = services.BuildServiceProvider();
    }

    [Fact]
    public async Task CreateOrder_CachesOrder()
    {
        var cache = _services.GetRequiredService<ICacheClient>();
        var service = new OrderService(cache);

        var order = await service.CreateOrderAsync(new CreateOrderRequest());

        var cached = await cache.GetAsync<Order>($"order:{order.Id}");
        Assert.True(cached.HasValue);
    }
}
```

## Best Practices

### 1. Proper Resource Disposal

Foundatio services implement `IDisposable` and/or `IAsyncDisposable`. The DI container handles disposal for registered services, but you must handle disposal correctly for manually created instances.

```csharp
// ✅ Good: DI container handles disposal
builder.Services.AddSingleton<ICacheClient, InMemoryCacheClient>();
// Container disposes when application shuts down

// ✅ Good: Using statement for short-lived instances
await using var cache = new InMemoryCacheClient();
await cache.SetAsync("key", "value");
// Automatically disposed

// ✅ Good: Manual disposal when needed
var queue = new InMemoryQueue<WorkItem>();
try
{
    await queue.EnqueueAsync(new WorkItem());
}
finally
{
    queue.Dispose();  // Or await using for IAsyncDisposable
}

// ❌ Bad: Not disposing manually created instances
var cache = new InMemoryCacheClient();
// ... use cache
// Never disposed - resources leak!
```

### 2. Async Disposal with `await using`

For services implementing `IAsyncDisposable`, prefer `await using`:

```csharp
// Locks implement IAsyncDisposable
await using var lck = await locker.AcquireAsync("resource");
if (lck is null)
    throw new InvalidOperationException("Failed to acquire lock on 'resource'");

await DoWork();
// Lock automatically released

// Queue entries should be completed/abandoned
var entry = await queue.DequeueAsync();
try
{
    await ProcessAsync(entry.Value);
    await entry.CompleteAsync();
}
catch
{
    await entry.AbandonAsync();
    throw;
}
```

### 3. Use Interfaces for Dependencies

```csharp
// ✅ Good: Interface dependency
public class OrderService
{
    private readonly ICacheClient _cache;

    public OrderService(ICacheClient cache)
    {
        _cache = cache;
    }
}

// ❌ Bad: Concrete dependency
public class OrderService
{
    private readonly RedisCacheClient _cache;  // Harder to test
}
```

### 4. Avoid Service Locator Pattern

```csharp
// ✅ Good: Constructor injection
public class MyService
{
    private readonly ICacheClient _cache;

    public MyService(ICacheClient cache)
    {
        _cache = cache;
    }
}

// ❌ Bad: Service locator
public class MyService
{
    private readonly IServiceProvider _services;

    public void DoWork()
    {
        var cache = _services.GetService<ICacheClient>();
    }
}
```

### 5. Register as Singletons When Appropriate

```csharp
// Stateless services that maintain connections
builder.Services.AddSingleton<ICacheClient>(...);
builder.Services.AddSingleton<IMessageBus>(...);

// Not scoped unless you need tenant isolation
```

### 6. Validate Configuration at Startup

```csharp
builder.Services.AddSingleton<ICacheClient>(sp =>
{
    var connectionString = builder.Configuration["Redis:ConnectionString"];
    if (string.IsNullOrEmpty(connectionString))
        throw new InvalidOperationException("Redis connection string not configured");

    var redis = ConnectionMultiplexer.Connect(connectionString);
    return new RedisCacheClient(o => o.ConnectionMultiplexer = redis);
});
```

## Next Steps

- [Configuration](./configuration) - Configuration options for Foundatio services
- [Caching](./caching) - Deep dive into caching
- [Getting Started](./getting-started) - Initial setup guide
