# In-Memory Implementations

Foundatio provides in-memory implementations for all core abstractions. These are perfect for development, testing, and single-process applications. In L1/L2 caching terminology, these serve as L1 (local) caches.

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `InMemoryCacheClient` | `ICacheClient` | Foundatio |
| `InMemoryMessageTransport` / `MessageBus` | `IMessageTransport` / `IMessageBus` | Foundatio |
| `InMemoryJobRuntimeStore` | `IJobRuntimeStore` | Foundatio |
| `InMemoryFileStorage` | `IFileStorage` | Foundatio |
| `CacheLockProvider` | `ILockProvider` | Foundatio |

## Installation

In-memory implementations are included in the core Foundatio package:

```bash
dotnet add package Foundatio
```

## InMemoryCacheClient

A high-performance in-memory cache (L1) with optional LRU eviction and memory-based limits. When used with `HybridCacheClient`, this serves as the L1 (local) tier in an L1/L2 caching architecture.

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
    options.MaxItems = 1000;

    // Clone values on get/set (prevents reference sharing bugs)
    options.CloneValues = true;

    // Logger factory
    options.LoggerFactory = loggerFactory;

    // Time provider (useful for testing)
    options.TimeProvider = TimeProvider.System;
});
```

### Value Cloning

The `CloneValues` option controls whether cached values are cloned on read and write operations to prevent reference sharing bugs. See [Caching Guide - Value Cloning](/guide/caching#clonevalues) for complete documentation including the problem, solution, performance trade-offs, and best practices.

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

## Messaging and durable job contracts

```csharp
services.AddFoundatio()
    .Messaging.UseInMemory()
    .Messaging.AddConsumer<WorkItem, WorkItemHandler>()
    .Messaging.AddSubscriber<OrderCreated, OrderCreatedHandler>("billing")
    .Jobs.UseInMemory()
    .Jobs.AddJobType<CleanupJob>("cleanup.v1");
services.AddMessageConsumers();
services.AddJobWorker();
```

In-memory messaging provides competing queues, named event subscriptions, temporary expiring subscriptions, lease supervision, and non-destructive dead-letter administration. Use `MessageBus` over `InMemoryMessageTransport` for manual construction. It has no native delayed sends; configure a dispatch store and `AddScheduledMessageDispatcher` for delays.

The job store uses the same claims, retries, cancellation, schedule revisions, pagination, and retention contracts as the Redis provider. All state is lost on process exit. These implementations exercise application behavior without requiring a broker; they do not model a distributed provider's durability or every capability.

See [Messaging](../messaging.md), [Durable jobs](../jobs.md), and the `Foundatio.Testing` harnesses for executable usage patterns.

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
var messageBus = new MessageBus(new InMemoryMessageTransport());
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

## Complete in-memory setup

```csharp
builder.Services.AddFoundatio()
    .Caching.UseInMemory()
    .Storage.UseInMemory()
    .Locking.UseCache()
    .Messaging.UseInMemory()
    .Messaging.AddConsumer<WorkItem, WorkItemHandler>()
    .Jobs.UseInMemory()
    .Jobs.AddJobType<CleanupJob>("cleanup.v1");
builder.Services.AddMessageConsumers();
builder.Services.AddJobWorker();
```

Clients and stores are singletons; handlers/jobs receive a scope for each invocation. Add a scheduler or scheduled-message dispatcher only when this process should run that role.

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
