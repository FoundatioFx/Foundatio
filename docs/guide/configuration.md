# Configuration

This guide covers configuration options for various Foundatio components.

## Cache Configuration

### InMemoryCacheClient

```csharp
var cache = new InMemoryCacheClient(options =>
{
    // Maximum number of items (LRU eviction)
    options.MaxItems = 1000;

    // Clone values on get/set (default: true)
    options.CloneValues = true;

    // Expiration scan frequency
    options.ExpirationScanFrequency = TimeSpan.FromMinutes(1);

    // Logger
    options.LoggerFactory = loggerFactory;

    // Time provider for testing
    options.TimeProvider = timeProvider;
});
```

### HybridCacheClient

```csharp
var hybridCache = new HybridCacheClient(
    redisCacheClient,
    redisMessageBus,
    new InMemoryCacheClientOptions
    {
        MaxItems = 500,
        LoggerFactory = loggerFactory
    }
);
```

### ScopedCacheClient

```csharp
var scopedCache = new ScopedCacheClient(
    cache: baseCacheClient,
    scope: "tenant:123"
);
```

## Messaging and worker queue configuration

Configure the bus once, then register explicit consumers or subscribers. Queue and topic capabilities differ by provider; unsupported delays require a scheduled-dispatch store and an explicitly hosted dispatcher.

```csharp
builder.Services.AddFoundatioWorker(foundatio => foundatio
    .Messaging.ConfigureRetry(policy => policy with { MaxAttempts = 5 })
    .UseInMemory()
    .AddConsumer<WorkItem, WorkItemHandler>(options => options.MaxConcurrency = 4));
```

Use `MessageBusOptions` when constructing a bus manually. Set `Topology` to `Ensure`, `Validate`, or `None`; set `Serializer` and matching `ContentType` when overriding serialization. Consumer concurrency belongs to an endpoint. Named subscriptions are durable; unnamed temporary subscriptions require provider support.

See [Messaging](messaging.md) for full configuration and provider limits. The former `InMemoryQueue`, `RedisQueue`, and publish-only message bus options do not configure the new transport runtime.

## Lock Configuration

### CacheLockProvider

```csharp
var locker = new CacheLockProvider(
    cache: cacheClient,
    messageBus: messageBus,
    options =>
    {
        // Default lock expiration
        options.DefaultTimeToLive = TimeSpan.FromMinutes(5);

        // Logger
        options.LoggerFactory = loggerFactory;
    }
);
```

### ThrottlingLockProvider

```csharp
var throttler = new ThrottlingLockProvider(
    cache: cacheClient,
    maxHits: 100,                    // Maximum operations
    period: TimeSpan.FromMinutes(1)  // Per time period
);
```

## Storage Configuration

### FolderFileStorage

```csharp
var storage = new FolderFileStorage(options =>
{
    // Root folder path
    options.Folder = "/data/files";

    // Logger
    options.LoggerFactory = loggerFactory;

    // Serializer
    options.Serializer = serializer;
});
```

### InMemoryFileStorage

```csharp
var storage = new InMemoryFileStorage(options =>
{
    // Maximum number of files
    options.MaxFiles = 1000;

    // Maximum total size
    options.MaxFileSize = 100 * 1024 * 1024; // 100MB

    // Logger
    options.LoggerFactory = loggerFactory;

    // Serializer
    options.Serializer = serializer;
});
```

### AzureFileStorage

```csharp
var storage = new AzureFileStorage(options =>
{
    // Connection string
    options.ConnectionString = "DefaultEndpointsProtocol=https;...";

    // Container name
    options.ContainerName = "files";

    // Logger
    options.LoggerFactory = loggerFactory;

    // Serializer
    options.Serializer = serializer;
});
```

### S3FileStorage

```csharp
var storage = new S3FileStorage(options =>
{
    // AWS region
    options.Region = RegionEndpoint.USEast1;

    // Bucket name
    options.Bucket = "my-files";

    // Optional credentials (uses default chain if not specified)
    options.AccessKey = "...";
    options.SecretKey = "...";

    // Logger
    options.LoggerFactory = loggerFactory;
});
```

## Resilience Configuration

### ResiliencePolicy

```csharp
var policy = new ResiliencePolicy
{
    // Maximum attempts
    MaxAttempts = 5,

    // Fixed delay between retries
    Delay = TimeSpan.FromSeconds(2),

    // Or exponential delay
    GetDelay = ResiliencePolicy.ExponentialDelay(TimeSpan.FromSeconds(1)),

    // Maximum delay cap
    MaxDelay = TimeSpan.FromMinutes(1),

    // Add jitter
    UseJitter = true,

    // Overall timeout
    Timeout = TimeSpan.FromMinutes(5),

    // Exceptions to not retry
    UnhandledExceptions = { typeof(OperationCanceledException) },

    // Custom retry logic
    ShouldRetry = (attempt, ex) => ex is TransientException,

    // Logger
    Logger = logger
};
```

### CircuitBreaker

```csharp
var circuitBreaker = new CircuitBreakerBuilder()
    // Failure threshold (0.0 - 1.0)
    .WithFailureRatio(0.5)

    // Minimum calls before evaluating
    .WithMinimumCalls(10)

    // Duration to keep circuit open
    .WithBreakDuration(TimeSpan.FromMinutes(1))

    // Success threshold for half-open recovery
    .WithSuccessThreshold(3)

    // Sampling duration for failure rate
    .WithSamplingDuration(TimeSpan.FromMinutes(5))

    .Build();
```

## Job configuration

Register the store and eligible job types in a worker:

```csharp
builder.Services.AddFoundatioWorker(foundatio => foundatio
    .Jobs.UseInMemory()
    .AddJobType<CleanupJob>("cleanup.v1")
    .AddCronJob<CleanupJob>("0 2 * * *"), jobConcurrency: 4);
```

Set per-request `MaxAttempts` in `JobRequestOptions`; schedule definitions snapshot their own retry budget. Persisted schedule edits use revisions, and changed declarations require a higher `ConfigurationVersion`. See [Durable jobs](jobs.md) for retention, capacity, and deployment behavior.

## Serialization Configuration

### Custom Serializer

```csharp
// Using System.Text.Json
var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
});

// Apply to services
var cache = new InMemoryCacheClient(o => o.Serializer = serializer);
var bus = new MessageBus(new InMemoryMessageTransport(), new MessageBusOptions { Serializer = serializer });
var storage = new InMemoryFileStorage(o => o.Serializer = serializer);
```

## Logging Configuration

### Configure Logging

```csharp
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("Foundatio", LogLevel.Debug);
});

// Apply to services
var cache = new InMemoryCacheClient(o => o.LoggerFactory = loggerFactory);
var bus = new MessageBus(new InMemoryMessageTransport(), new MessageBusOptions { LoggerFactory = loggerFactory });
```

## Environment Variables

### Common Environment Variables

```bash
# Redis connection
FOUNDATIO_REDIS_CONNECTION=localhost:6379

# Azure Storage
FOUNDATIO_AZURE_STORAGE_CONNECTION=DefaultEndpointsProtocol=https;...

# AWS
AWS_ACCESS_KEY_ID=...
AWS_SECRET_ACCESS_KEY=...
AWS_REGION=us-east-1
```

### Reading from Configuration

```csharp
var builder = WebApplication.CreateBuilder(args);

var redisConnection = builder.Configuration["Foundatio:Redis:Connection"];
var azureStorage = builder.Configuration["Foundatio:Azure:StorageConnection"];

builder.Services.AddSingleton<ICacheClient>(sp =>
{
    if (!string.IsNullOrEmpty(redisConnection))
    {
        var redis = ConnectionMultiplexer.Connect(redisConnection);
        return new RedisCacheClient(o => o.ConnectionMultiplexer = redis);
    }
    return new InMemoryCacheClient();
});
```

## appsettings.json Example

```json
{
  "Foundatio": {
    "Cache": {
      "Type": "Redis",
      "Connection": "localhost:6379",
      "MaxItems": 1000
    },
    "Queue": {
      "Type": "Redis",
      "WorkItemTimeout": "00:05:00",
      "Retries": 3
    },
    "Storage": {
      "Type": "Azure",
      "ConnectionString": "...",
      "ContainerName": "files"
    },
    "Resilience": {
      "MaxAttempts": 5,
      "InitialDelay": "00:00:01",
      "UseJitter": true
    }
  }
}
```

### Reading Configuration

```csharp
public class FoundatioOptions
{
    public CacheOptions Cache { get; set; }
    public QueueOptions Queue { get; set; }
    public StorageOptions Storage { get; set; }
    public ResilienceOptions Resilience { get; set; }
}

public class CacheOptions
{
    public string Type { get; set; }
    public string Connection { get; set; }
    public int MaxItems { get; set; }
}

// Registration
builder.Services.Configure<FoundatioOptions>(
    builder.Configuration.GetSection("Foundatio"));
```

## Best Practices

### 1. Use Options Pattern

```csharp
// Define options class
public class CacheOptions
{
    public string Type { get; set; } = "InMemory";
    public int MaxItems { get; set; } = 1000;
    public string RedisConnection { get; set; }
}

// Register and use
builder.Services.Configure<CacheOptions>(
    builder.Configuration.GetSection("Cache"));

builder.Services.AddSingleton<ICacheClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
    return options.Type == "Redis"
        ? new RedisCacheClient(...)
        : new InMemoryCacheClient(o => o.MaxItems = options.MaxItems);
});
```

### 2. Validate Configuration

```csharp
builder.Services.AddOptions<CacheOptions>()
    .Bind(builder.Configuration.GetSection("Cache"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

### 3. Use Environment-Specific Settings

```bash
appsettings.json              # Base settings
appsettings.Development.json  # In-memory implementations
appsettings.Production.json   # Redis/Azure implementations
```

### 4. Keep Secrets Secure

```csharp
// Use secrets manager
var redis = builder.Configuration["Redis:ConnectionString"];

// Or environment variables
var redis = Environment.GetEnvironmentVariable("REDIS_CONNECTION");
```

## Next Steps

- [Dependency Injection](./dependency-injection) - Service registration patterns
- [Getting Started](./getting-started) - Initial setup guide
- [Caching](./caching) - Cache configuration details
