# In-Memory Implementations

Foundatio provides in-memory implementations for all core abstractions. These are perfect for development, testing, and single-process applications.

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `InMemoryCacheClient` | `ICacheClient` | Foundatio |
| `InMemoryQueue<T>` | `IQueue<T>` | Foundatio |
| `InMemoryMessageBus` | `IMessageBus` | Foundatio |
| `InMemoryFileStorage` | `IFileStorage` | Foundatio |
| `CacheLockProvider` | `ILockProvider` | Foundatio |

## Installation

In-memory implementations are included in the core Foundatio package:

```bash
dotnet add package Foundatio
```

## InMemoryCacheClient

A high-performance in-memory cache with optional LRU eviction and memory-based limits.

### Basic Usage

```csharp
using Foundatio.Caching;

var cache = new InMemoryCacheClient();

// Store and retrieve values
await cache.SetAsync("key", "value");
var value = await cache.GetAsync<string>("key");

// With expiration
await cache.SetAsync("temp", "data", TimeSpan.FromMinutes(5));
```

### Configuration Options

```csharp
var cache = new InMemoryCacheClient(options =>
{
    // Maximum items (enables LRU eviction)
    options.MaxItems(1000);

    // Clone values on get/set (thread safety)
    options.CloneValues(true);

    // Logger factory
    options.LoggerFactory(loggerFactory);

    // Time provider (useful for testing)
    options.TimeProvider(TimeProvider.System);
});
```

### Features

- **LRU Eviction**: Automatically removes least recently used items when `MaxItems` is reached
- **Memory-Based Eviction**: Limit cache by memory consumption with `WithDynamicSizing()` or `WithFixedSizing()`
- **Expiration**: Items can have absolute or sliding expiration
- **Value Cloning**: Optionally clone values to prevent reference sharing issues
- **Thread-Safe**: All operations are thread-safe

### Memory-Based Eviction

Limit cache size by memory consumption with intelligent size-aware eviction. When the cache exceeds the memory limit, it evicts items based on a combination of size, age, and access recency.

```csharp
// Dynamic sizing: Automatically calculates entry sizes (recommended for mixed object types)
var cache = new InMemoryCacheClient(o => o
    .WithDynamicSizing(maxMemorySize: 100 * 1024 * 1024) // 100 MB limit
    .MaxItems(10000)); // Optional: also limit by item count

// Fixed sizing: Maximum performance when entries are uniform size
var fixedSizeCache = new InMemoryCacheClient(o => o
    .WithFixedSizing(
        maxMemorySize: 50 * 1024 * 1024,  // 50 MB limit
        averageEntrySize: 1024));          // Assume 1KB per entry

// Check current memory usage
Console.WriteLine($"Memory: {cache.CurrentMemorySize:N0} / {cache.MaxMemorySize:N0} bytes");
```

**How dynamic sizing works:**

- Uses fast paths for common types (strings, primitives, arrays)
- Falls back to JSON serialization for complex objects
- Caches type size calculations for performance

### Per-Entry Size Limits

Prevent individual large entries from consuming too much cache space:

```csharp
// Skip oversized entries (default behavior)
var cache = new InMemoryCacheClient(o => o
    .WithDynamicSizing(100 * 1024 * 1024)  // 100 MB total
    .MaxEntrySize(1 * 1024 * 1024));        // 1 MB per entry limit

// Entries exceeding MaxEntrySize are skipped (not cached) and a warning is logged
var result = await cache.SetAsync("large-data", veryLargeObject);
// result = false if entry exceeds MaxEntrySize

// Strict mode: Throw exception on oversized entries
var strictCache = new InMemoryCacheClient(o => o
    .WithDynamicSizing(100 * 1024 * 1024)
    .MaxEntrySize(1 * 1024 * 1024)
    .ShouldThrowOnMaxEntrySizeExceeded()); // Throws MaxEntrySizeExceededCacheException

try
{
    await strictCache.SetAsync("large-data", veryLargeObject);
}
catch (MaxEntrySizeExceededCacheException ex)
{
    // Handle oversized entry
    _logger.LogError(ex, "Entry too large for cache: {EntrySize} > {MaxEntrySize}",
        ex.EntrySize, ex.MaxEntrySize);
}
```

**When to use MaxEntrySize:**

- **API response caching**: Prevent a single large response from evicting many smaller cached items
- **User data caching**: Limit impact of users with unusually large data
- **Memory protection**: Guard against unbounded object growth

### DI Registration

```csharp
// Simple registration
services.AddSingleton<ICacheClient, InMemoryCacheClient>();

// With configuration
services.AddSingleton<ICacheClient>(sp =>
    new InMemoryCacheClient(options =>
    {
        options.MaxItems = 1000;
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## InMemoryQueue

A thread-safe in-memory queue with retry support and dead letter handling.

### Basic Usage

```csharp
using Foundatio.Queues;

var queue = new InMemoryQueue<WorkItem>();

// Enqueue items
await queue.EnqueueAsync(new WorkItem { Id = 1, Data = "Hello" });

// Dequeue and process
var entry = await queue.DequeueAsync();
if (entry != null)
{
    // Process the item
    Console.WriteLine(entry.Value.Data);

    // Mark as complete
    await entry.CompleteAsync();
}
```

### Configuration Options

```csharp
var queue = new InMemoryQueue<WorkItem>(options =>
{
    // Queue identifier
    options.Name = "work-items";

    // Work item timeout (for retry)
    options.WorkItemTimeout = TimeSpan.FromMinutes(5);

    // Retry settings
    options.Retries = 3;
    options.RetryDelay = TimeSpan.FromSeconds(30);

    // Processing behaviors
    options.Behaviors.Add(new EnqueueAbandonedQueueEntryBehavior());

    // Logger
    options.LoggerFactory = loggerFactory;
});
```

### Processing Patterns

```csharp
// Continuous processing with handler
await queue.StartWorkingAsync(async (entry, token) =>
{
    await ProcessWorkItemAsync(entry.Value);
});

// Process until empty
while (await queue.GetQueueStatsAsync() is { Queued: > 0 })
{
    var entry = await queue.DequeueAsync();
    await entry.CompleteAsync();
}
```

### DI Registration

```csharp
services.AddSingleton<IQueue<WorkItem>>(sp =>
    new InMemoryQueue<WorkItem>(options =>
    {
        options.Name = "work-items";
        options.WorkItemTimeout = TimeSpan.FromMinutes(5);
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## InMemoryMessageBus

A simple in-memory pub/sub message bus for single-process communication.

### Basic Usage

```csharp
using Foundatio.Messaging;

var messageBus = new InMemoryMessageBus();

// Subscribe to messages
await messageBus.SubscribeAsync<UserCreatedEvent>(message =>
{
    Console.WriteLine($"User created: {message.UserId}");
});

// Publish messages
await messageBus.PublishAsync(new UserCreatedEvent { UserId = "123" });
```

### Configuration Options

```csharp
var messageBus = new InMemoryMessageBus(options =>
{
    options.LoggerFactory = loggerFactory;
    options.Serializer = serializer;
});
```

### Subscription Management

```csharp
// Subscribe with options
await messageBus.SubscribeAsync<OrderEvent>(
    handler: async (message, token) =>
    {
        await ProcessOrderAsync(message);
    },
    cancellationToken: stoppingToken);

// Type hierarchy subscription
await messageBus.SubscribeAsync<BaseEvent>(message =>
{
    // Receives all events that inherit from BaseEvent
});
```

### DI Registration

```csharp
services.AddSingleton<IMessageBus, InMemoryMessageBus>();
services.AddSingleton<IMessagePublisher>(sp =>
    sp.GetRequiredService<IMessageBus>());
services.AddSingleton<IMessageSubscriber>(sp =>
    sp.GetRequiredService<IMessageBus>());
```

## InMemoryFileStorage

An in-memory file storage implementation with optional size limits.

### Basic Usage

```csharp
using Foundatio.Storage;

var storage = new InMemoryFileStorage();

// Save files
await storage.SaveFileAsync("documents/file.txt", "Hello, World!");

// Read files
var content = await storage.GetFileContentsAsync("documents/file.txt");

// List files
var files = await storage.GetFileListAsync("documents/");
```

### Configuration Options

```csharp
var storage = new InMemoryFileStorage(options =>
{
    // Maximum number of files
    options.MaxFiles = 1000;

    // Maximum file size
    options.MaxFileSize = 100 * 1024 * 1024; // 100MB

    // Logger
    options.LoggerFactory = loggerFactory;

    // Serializer for metadata
    options.Serializer = serializer;
});
```

### File Operations

```csharp
// Save from stream
using var stream = File.OpenRead("local-file.txt");
await storage.SaveFileAsync("remote/file.txt", stream);

// Get file info
var spec = await storage.GetFileInfoAsync("remote/file.txt");
Console.WriteLine($"Size: {spec?.Size}, Modified: {spec?.Modified}");

// Check existence
if (await storage.ExistsAsync("remote/file.txt"))
{
    // File exists
}

// Delete files
await storage.DeleteFileAsync("remote/file.txt");
await storage.DeleteFilesAsync("temp/"); // Delete by pattern
```

### DI Registration

```csharp
services.AddSingleton<IFileStorage>(sp =>
    new InMemoryFileStorage(options =>
    {
        options.MaxFiles = 1000;
        options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
    }));
```

## CacheLockProvider (In-Memory Locks)

Use `CacheLockProvider` with `InMemoryCacheClient` for in-memory distributed locks.

### Basic Usage

```csharp
using Foundatio.Lock;

var cache = new InMemoryCacheClient();
var messageBus = new InMemoryMessageBus();
var locker = new CacheLockProvider(cache, messageBus);

// Acquire a lock
await using var lockHandle = await locker.AcquireAsync("resource-key");
if (lockHandle != null)
{
    // Do exclusive work
}
```

### Configuration Options

```csharp
var locker = new CacheLockProvider(cache, messageBus, options =>
{
    // Default lock duration
    options.DefaultTimeToLive = TimeSpan.FromMinutes(5);

    // Logger
    options.LoggerFactory = loggerFactory;
});
```

### DI Registration

```csharp
services.AddSingleton<ILockProvider>(sp =>
    new CacheLockProvider(
        sp.GetRequiredService<ICacheClient>(),
        sp.GetRequiredService<IMessageBus>()));
```

## Complete In-Memory Setup

### All Services

```csharp
public static IServiceCollection AddFoundatioInMemory(
    this IServiceCollection services)
{
    // Cache
    services.AddSingleton<ICacheClient, InMemoryCacheClient>();

    // Message Bus
    services.AddSingleton<IMessageBus, InMemoryMessageBus>();
    services.AddSingleton<IMessagePublisher>(sp =>
        sp.GetRequiredService<IMessageBus>());
    services.AddSingleton<IMessageSubscriber>(sp =>
        sp.GetRequiredService<IMessageBus>());

    // Lock Provider
    services.AddSingleton<ILockProvider>(sp =>
        new CacheLockProvider(
            sp.GetRequiredService<ICacheClient>(),
            sp.GetRequiredService<IMessageBus>()));

    // File Storage
    services.AddSingleton<IFileStorage, InMemoryFileStorage>();

    return services;
}

// With queues
public static IServiceCollection AddFoundatioQueue<T>(
    this IServiceCollection services,
    string name) where T : class
{
    services.AddSingleton<IQueue<T>>(sp =>
        new InMemoryQueue<T>(options =>
        {
            options.Name = name;
            options.LoggerFactory = sp.GetRequiredService<ILoggerFactory>();
        }));

    return services;
}
```

### Usage

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFoundatioInMemory();
builder.Services.AddFoundatioQueue<WorkItem>("work-items");
builder.Services.AddFoundatioQueue<EmailMessage>("emails");

var app = builder.Build();
```

## When to Use In-Memory

### ✅ Good Use Cases

- **Local Development**: No external dependencies needed
- **Unit Testing**: Fast, isolated tests
- **Single-Process Apps**: Simple deployments
- **Prototyping**: Quick iterations
- **Small Workloads**: Low-traffic applications

### ⚠️ Limitations

- **No Persistence**: Data lost on restart
- **Single Process**: No cross-process communication
- **Memory Bound**: Limited by available RAM
- **No Clustering**: Not suitable for distributed systems

### Switching to Production

```csharp
// Easy to swap implementations
if (builder.Environment.IsDevelopment())
{
    services.AddSingleton<ICacheClient, InMemoryCacheClient>();
}
else
{
    services.AddSingleton<ICacheClient>(sp =>
        new RedisCacheClient(options =>
        {
            options.ConnectionMultiplexer =
                ConnectionMultiplexer.Connect("redis:6379");
        }));
}
```

## Testing with In-Memory

```csharp
public class CacheTests
{
    [Fact]
    public async Task ShouldCacheAndRetrieveValue()
    {
        // Arrange
        var cache = new InMemoryCacheClient();

        // Act
        await cache.SetAsync("key", "value");
        var result = await cache.GetAsync<string>("key");

        // Assert
        Assert.Equal("value", result);
    }
}
```

## Next Steps

- [Redis Implementation](./redis) - Production-ready distributed caching
- [Azure Implementation](./azure) - Cloud-native Azure services
- [AWS Implementation](./aws) - Amazon Web Services integration
