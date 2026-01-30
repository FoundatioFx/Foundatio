# Resilience

Resilience policies provide a powerful way to handle transient failures and make your applications more robust. Foundatio's resilience system includes retry logic, circuit breakers, timeouts, and exponential backoff.

## The IResiliencePolicy Interface

```csharp
public interface IResiliencePolicy
{
    // Synchronous methods
    void Execute(Action<CancellationToken> action, CancellationToken cancellationToken = default);
    TResult Execute<TResult>(Func<CancellationToken, TResult> action, CancellationToken cancellationToken = default);

    // Asynchronous methods
    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default);

    // State-based overloads (zero allocations)
    void Execute<TState>(TState state, Action<TState, CancellationToken> action, CancellationToken cancellationToken = default);
    TResult Execute<TState, TResult>(TState state, Func<TState, CancellationToken, TResult> action, CancellationToken cancellationToken = default);
    ValueTask ExecuteAsync<TState>(TState state, Func<TState, CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);
    ValueTask<TResult> ExecuteAsync<TState, TResult>(TState state, Func<TState, CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default);
}
```

## Basic Usage

### Creating a Policy

```csharp
using Foundatio.Resilience;

var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))
    .WithJitter()
    .Build();
```

### Executing with Retry (Async)

```csharp
await policy.ExecuteAsync(async ct =>
{
    await SomeUnreliableOperationAsync(ct);
});
```

### Executing with Retry (Sync)

```csharp
policy.Execute(ct =>
{
    SomeUnreliableOperation();
});
```

### With Return Values

```csharp
// Async
var result = await policy.ExecuteAsync(async ct =>
{
    return await GetDataFromApiAsync(ct);
});

// Sync
var result = policy.Execute(ct =>
{
    return GetDataFromDatabase();
});
```

### Zero-Allocation Execution (State-Based)

For performance-critical paths, use state-based overloads to avoid closure allocations:

```csharp
// Pass state explicitly instead of capturing in a closure
var userId = 42;
var result = await policy.ExecuteAsync(userId, async (id, ct) =>
{
    return await GetUserAsync(id, ct);
});

// Sync version
policy.Execute(userId, (id, ct) =>
{
    ProcessUser(id);
});
```

## ResiliencePolicyBuilder

### Retry Configuration

```csharp
var policy = new ResiliencePolicyBuilder()
    // Maximum number of attempts (default: 3)
    .WithMaxAttempts(5)

    // Fixed delay between retries
    .WithDelay(TimeSpan.FromSeconds(2))

    // Or exponential delay (doubles each retry)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))

    // Or linear delay (adds fixed amount each retry)
    .WithLinearDelay(TimeSpan.FromSeconds(1))

    // Maximum delay cap
    .WithMaxDelay(TimeSpan.FromMinutes(1))

    // Add randomness to prevent thundering herd
    .WithJitter()

    .Build();
```

### Custom Delay Function

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    .WithDelayFunction(attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)))
    .Build();
```

### Timeout Configuration

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(3)
    .WithTimeout(TimeSpan.FromSeconds(30))  // Overall timeout
    .Build();
```

### Conditional Retry

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))
    .WithShouldRetry((attempt, exception) =>
    {
        // Only retry on specific exceptions
        return exception is HttpRequestException or TimeoutException;
    })
    .Build();
```

### Unhandled Exceptions

By default, `OperationCanceledException` and `BrokenCircuitException` are never retried. You can add additional exception types:

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    // These exceptions will be thrown immediately without retry
    .WithUnhandledException<ArgumentException>()
    .WithUnhandledException<InvalidOperationException>()
    .Build();
```

## Circuit Breaker

Prevent cascading failures by temporarily stopping calls to failing services:

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(3)
    .WithCircuitBreaker(cb => cb
        // Open circuit after 50% failure rate
        .WithFailureRatio(0.5)
        // Need at least 10 calls before evaluating
        .WithMinimumCalls(10)
        // Keep circuit open for 1 minute
        .WithBreakDuration(TimeSpan.FromMinutes(1)))
    .Build();
```

### Circuit Breaker States

```txt
                    ┌────────────────┐
                    │     Closed     │
                    │ (Normal Ops)   │
                    └───────┬────────┘
                            │
              Failures      │      All calls
              exceed        │      pass through
              threshold     │
                            │
                            ▼
                    ┌────────────────┐
              ┌────▶│      Open      │
              │     │ (Fail Fast)    │
              │     └───────┬────────┘
              │             │
    Test call │             │ Break duration
    fails     │             │ expires
              │             │
              │             ▼
              │     ┌────────────────┐
              └─────│   Half-Open    │
                    │  (Testing)     │
                    └───────┬────────┘
                            │
                            │ Test call
                            │ succeeds
                            │
                            ▼
                    ┌────────────────┐
                    │     Closed     │
                    └────────────────┘
```

- **Closed**: Normal operation, calls pass through
- **Open**: Calls fail immediately without execution
- **Half-Open**: Single test call allowed to check recovery

### Checking Circuit State

```csharp
var policy = new ResiliencePolicy();
policy.CircuitBreaker = new CircuitBreaker(new CircuitBreakerBuilder()
    .WithFailureRatio(0.5)
    .WithMinimumCalls(10)
    .WithBreakDuration(TimeSpan.FromMinutes(1)));

// Check state
if (policy.CircuitBreaker.State == CircuitState.Open)
{
    _logger.LogWarning("Circuit is open - skipping operation");
    return;
}

await policy.ExecuteAsync(async ct =>
{
    await CallExternalServiceAsync(ct);
});
```

## ResiliencePolicy Properties

```csharp
var policy = new ResiliencePolicy
{
    // Maximum retry attempts
    MaxAttempts = 5,

    // Fixed delay between retries
    Delay = TimeSpan.FromSeconds(2),

    // Custom delay function
    GetDelay = ResiliencePolicy.ExponentialDelay(TimeSpan.FromSeconds(1)),

    // Maximum delay cap
    MaxDelay = TimeSpan.FromMinutes(1),

    // Add jitter to delays
    UseJitter = true,

    // Overall timeout
    Timeout = TimeSpan.FromMinutes(5),

    // Circuit breaker
    CircuitBreaker = new CircuitBreaker(...),

    // Exceptions that should not be retried
    UnhandledExceptions = { typeof(OperationCanceledException) },

    // Custom retry logic
    ShouldRetry = (attempt, ex) => ex is TransientException,

    // Logger for retry events
    Logger = logger
};
```

## Static Delay Functions

Foundatio provides built-in delay functions:

```csharp
// Exponential: 1s, 2s, 4s, 8s, 16s...
ResiliencePolicy.ExponentialDelay(TimeSpan.FromSeconds(1))

// Linear: 1s, 2s, 3s, 4s, 5s...
ResiliencePolicy.LinearDelay(TimeSpan.FromSeconds(1))

// Constant: 2s, 2s, 2s, 2s...
ResiliencePolicy.ConstantDelay(TimeSpan.FromSeconds(2))
```

## IResiliencePolicyProvider

Manage multiple named policies:

```csharp
using Foundatio.Resilience;

var provider = new ResiliencePolicyProviderBuilder()
    // Default policy for unspecified operations
    .WithDefaultPolicy(builder => builder
        .WithMaxAttempts(3)
        .WithExponentialDelay(TimeSpan.FromSeconds(1)))

    // Named policy for external APIs
    .WithPolicy("external-api", builder => builder
        .WithMaxAttempts(5)
        .WithCircuitBreaker()
        .WithTimeout(TimeSpan.FromSeconds(30)))

    // Named policy for database operations
    .WithPolicy("database", builder => builder
        .WithMaxAttempts(3)
        .WithLinearDelay(TimeSpan.FromMilliseconds(100))
        .WithUnhandledException<ArgumentException>())

    .Build();

// Get and use policies
var apiPolicy = provider.GetPolicy("external-api");
await apiPolicy.ExecuteAsync(async ct =>
{
    await CallExternalApiAsync(ct);
});

var dbPolicy = provider.GetPolicy("database");
await dbPolicy.ExecuteAsync(async ct =>
{
    await SaveToDbAsync(ct);
});
```

## Type-Based Policies

Get policies based on service type:

```csharp
var provider = new ResiliencePolicyProviderBuilder()
    .WithPolicy<IExternalApiClient>(builder => builder
        .WithMaxAttempts(5)
        .WithCircuitBreaker())
    .WithPolicy<IDatabaseService>(builder => builder
        .WithMaxAttempts(3)
        .WithLinearDelay())
    .Build();

var policy = provider.GetPolicy<IExternalApiClient>();
```

## Common Patterns

### HTTP Client with Resilience

```csharp
public class ResilientHttpClient
{
    private readonly HttpClient _client;
    private readonly IResiliencePolicy _policy;

    public ResilientHttpClient(HttpClient client)
    {
        _client = client;
        _policy = new ResiliencePolicyBuilder()
            .WithMaxAttempts(3)
            .WithExponentialDelay(TimeSpan.FromSeconds(1))
            .WithCircuitBreaker(cb => cb
                .WithFailureRatio(0.5)
                .WithMinimumCalls(10)
                .WithBreakDuration(TimeSpan.FromMinutes(1)))
            .WithTimeout(TimeSpan.FromSeconds(30))
            .WithShouldRetry((_, ex) =>
                ex is HttpRequestException or TaskCanceledException)
            .Build();
    }

    public async Task<T> GetAsync<T>(string url)
    {
        return await _policy.ExecuteAsync(async ct =>
        {
            var response = await _client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ct);
        });
    }
}
```

### Database Operations

```csharp
public class ResilientRepository
{
    private readonly IResiliencePolicy _policy;

    public ResilientRepository()
    {
        _policy = new ResiliencePolicyBuilder()
            .WithMaxAttempts(3)
            .WithLinearDelay(TimeSpan.FromMilliseconds(100))
            .WithShouldRetry((_, ex) => IsTransientDbException(ex))
            .Build();
    }

    public async Task<User> GetUserAsync(int id)
    {
        return await _policy.ExecuteAsync(async ct =>
        {
            return await _context.Users.FindAsync(id, ct);
        });
    }

    private bool IsTransientDbException(Exception ex)
    {
        return ex is DbUpdateException or TimeoutException;
    }
}
```

### Graceful Degradation

```csharp
public class CatalogService
{
    private readonly IResiliencePolicy _policy;
    private readonly ICacheClient _cache;

    public async Task<Product> GetProductAsync(int id)
    {
        try
        {
            return await _policy.ExecuteAsync(async ct =>
            {
                return await _api.GetProductAsync(id, ct);
            });
        }
        catch (BrokenCircuitException)
        {
            // Fall back to cached data when circuit is open
            var cached = await _cache.GetAsync<Product>($"product:{id}");
            if (cached.HasValue)
            {
                _logger.LogWarning("Returning cached product {Id}", id);
                return cached.Value;
            }
            throw;
        }
    }
}
```

### Retry with Logging

```csharp
var policy = new ResiliencePolicy
{
    MaxAttempts = 5,
    GetDelay = ResiliencePolicy.ExponentialDelay(TimeSpan.FromSeconds(1)),
    Logger = loggerFactory.CreateLogger("Resilience")
};

// Logs will include attempt number, delay, and exception details
await policy.ExecuteAsync(async ct =>
{
    await UnreliableOperationAsync(ct);
});
```

## Integration with Foundatio

### Cache with Resilience

```csharp
var resilientCache = new ResilientCacheClient(
    new RedisCacheClient(...),
    new ResiliencePolicyBuilder()
        .WithMaxAttempts(3)
        .WithExponentialDelay(TimeSpan.FromMilliseconds(100))
        .Build()
);
```

### Queue with Resilience

```csharp
public class ResilientQueueProcessor
{
    private readonly IQueue<WorkItem> _queue;
    private readonly IResiliencePolicy _policy;

    public async Task ProcessAsync(WorkItem item)
    {
        await _policy.ExecuteAsync(async ct =>
        {
            await DoProcessingAsync(item, ct);
        });
    }
}
```

## Dependency Injection

### Register Policy Provider

```csharp
services.AddSingleton<IResiliencePolicyProvider>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience");

    return new ResiliencePolicyProviderBuilder()
        .WithDefaultPolicy(b => b
            .WithMaxAttempts(3)
            .WithExponentialDelay(TimeSpan.FromSeconds(1))
            .WithLogger(logger))
        .WithPolicy("http", b => b
            .WithMaxAttempts(5)
            .WithCircuitBreaker()
            .WithTimeout(TimeSpan.FromSeconds(30)))
        .Build();
});
```

### Use in Services

```csharp
public class MyService
{
    private readonly IResiliencePolicy _policy;

    public MyService(IResiliencePolicyProvider policyProvider)
    {
        _policy = policyProvider.GetPolicy("http");
    }
}
```

## Best Practices

### 1. Use Appropriate Timeouts

```csharp
// Match timeout to operation type
.WithTimeout(TimeSpan.FromSeconds(5))   // Fast operations
.WithTimeout(TimeSpan.FromSeconds(30))  // API calls
.WithTimeout(TimeSpan.FromMinutes(5))   // Long operations
```

### 2. Configure Circuit Breaker Thresholds

```csharp
.WithCircuitBreaker(cb => cb
    // High traffic: needs more samples
    .WithMinimumCalls(100)
    .WithFailureRatio(0.5)

    // Low traffic: fewer samples
    .WithMinimumCalls(10)
    .WithFailureRatio(0.3)
)
```

### 3. Use Jitter to Prevent Thundering Herd

```csharp
.WithExponentialDelay(TimeSpan.FromSeconds(1))
.WithJitter()  // Adds randomness to prevent synchronized retries
```

### 4. Handle Specific Exceptions

```csharp
.WithShouldRetry((attempt, ex) =>
{
    // Only retry transient failures
    return ex is HttpRequestException
        or TimeoutException
        or SocketException;
})
.WithUnhandledException<OperationCanceledException>()
.WithUnhandledException<ArgumentException>()
```

> **Note:** By default, `OperationCanceledException` and `BrokenCircuitException` are never retried. Additionally, Foundatio's built-in components automatically exclude their feature-specific exceptions from retries:
> - `MessageBusBase` excludes `MessageBusException`
> - `CacheLockProvider` excludes `CacheException`
>
> This ensures that deliberate application-level failures (like a blocked RabbitMQ connection or a cache operation error) are not wastefully retried.

### 5. Log Retry Attempts

```csharp
var policy = new ResiliencePolicy
{
    Logger = loggerFactory.CreateLogger("Resilience"),
    MaxAttempts = 5
};
// Automatically logs each retry attempt
```

## Performance

Foundatio's resilience implementation is optimized for high performance with minimal allocations.

### Allocation-Free Execution

Use the state-based overloads to achieve zero heap allocations in hot paths:

```csharp
// Instead of capturing variables in a closure (allocates):
var userId = GetUserId();
await policy.ExecuteAsync(async ct => await GetUserAsync(userId, ct));

// Pass state explicitly (zero allocations):
var userId = GetUserId();
await policy.ExecuteAsync(userId, async (id, ct) => await GetUserAsync(id, ct));
```

### Sync vs Async

Choose the appropriate execution method:

```csharp
// Use sync for CPU-bound or already-completed operations
var cachedValue = policy.Execute(_ => cache.Get(key));

// Use async for I/O-bound operations
var apiResult = await policy.ExecuteAsync(async ct => await api.CallAsync(ct));
```

### Benchmark Results

Foundatio consistently outperforms alternatives when retry policies are configured:

| Scenario                  | Foundatio      | Polly          | Foundatio Advantage                  |
| ------------------------- | -------------- | -------------- | ------------------------------------ |
| Sync with retries         | ~23 ns         | ~122 ns        | **5.3x faster**                      |
| Async with retries        | ~37 ns         | ~141 ns        | **3.8x faster**                      |
| State-based (zero-alloc)  | ~31 ns, 0 B    | ~131 ns, 88 B  | **4.2x faster, zero allocations**    |

Benchmarks run on AMD Ryzen 7 9800X3D, .NET 10.0

## Next Steps

- [Caching](./caching) - Combine with cache fallbacks
- [Queues](./queues) - Resilient queue processing
- [Jobs](./jobs) - Retry job execution
