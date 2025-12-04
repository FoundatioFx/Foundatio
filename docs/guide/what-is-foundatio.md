# What is Foundatio?

Foundatio is a modular .NET library providing pluggable building blocks for distributed applications, including:

- **Caching** - Fast data access with multiple backend implementations
- **Queues** - FIFO message delivery for background processing
- **Locks** - Distributed locking for resource coordination
- **Messaging** - Pub/sub patterns for event-driven architectures
- **Jobs** - Long-running process management
- **File Storage** - Abstracted file operations
- **Resilience** - Retry policies and circuit breakers

## Design Philosophy

Foundatio was built with several key principles in mind:

### Abstract Interfaces

All core functionality is exposed through clean interfaces (`ICacheClient`, `IQueue<T>`, `ILockProvider`, `IMessageBus`, `IFileStorage`). This allows you to:

- **Swap implementations** without changing application code
- **Test easily** using in-memory implementations
- **Scale gradually** by switching to distributed implementations when needed

### Dependency Injection First

Every component is designed to work seamlessly with Microsoft.Extensions.DependencyInjection:

```csharp
services.AddSingleton<ICacheClient>(sp => new InMemoryCacheClient());
services.AddSingleton<IMessageBus>(sp => new InMemoryMessageBus());
services.AddSingleton<ILockProvider>(sp => new CacheLockProvider(
    sp.GetRequiredService<ICacheClient>(),
    sp.GetRequiredService<IMessageBus>()
));
```

### Development-Production Parity

In-memory implementations for all abstractions mean:

- **No external dependencies** during development
- **Fast unit tests** without infrastructure setup
- **Same code paths** in development and production

### Extensibility

Each abstraction can be extended with custom implementations:

```csharp
public class MyCustomCacheClient : ICacheClient
{
    // Your custom implementation
}
```

## Core Abstractions

### Caching

Store and retrieve data with expiration support:

```csharp
ICacheClient cache = new InMemoryCacheClient();
await cache.SetAsync("user:123", user, TimeSpan.FromMinutes(30));
var cached = await cache.GetAsync<User>("user:123");
```

[Learn more about Caching →](./caching)

### Queues

Reliable message delivery with at-least-once semantics:

```csharp
IQueue<WorkItem> queue = new InMemoryQueue<WorkItem>();
await queue.EnqueueAsync(new WorkItem { Id = 1 });
var entry = await queue.DequeueAsync();
// Process and complete
await entry.CompleteAsync();
```

[Learn more about Queues →](./queues)

### Locks

Distributed locking for coordinating access:

```csharp
ILockProvider locker = new CacheLockProvider(cache, messageBus);
await using var @lock = await locker.AcquireAsync("my-resource");
if (@lock != null)
{
    // Exclusive access to resource
}
```

[Learn more about Locks →](./locks)

### Messaging

Publish/subscribe messaging:

```csharp
IMessageBus bus = new InMemoryMessageBus();
await bus.SubscribeAsync<OrderCreated>(msg => ProcessOrder(msg));
await bus.PublishAsync(new OrderCreated { OrderId = 123 });
```

[Learn more about Messaging →](./messaging)

### File Storage

Abstracted file operations:

```csharp
IFileStorage storage = new FolderFileStorage("/data");
await storage.SaveFileAsync("reports/2024/report.pdf", fileStream);
var file = await storage.GetFileStreamAsync("reports/2024/report.pdf");
```

[Learn more about Storage →](./storage)

### Jobs

Background job processing:

```csharp
public class MyJob : JobBase
{
    protected override Task<JobResult> RunInternalAsync(JobContext context)
    {
        // Do work
        return Task.FromResult(JobResult.Success);
    }
}
```

[Learn more about Jobs →](./jobs)

### Resilience

Retry policies with circuit breakers:

```csharp
var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(5)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))
    .WithCircuitBreaker()
    .Build();

await policy.ExecuteAsync(async ct => {
    await SomeUnreliableOperationAsync(ct);
});
```

[Learn more about Resilience →](./resilience)

## When to Use Foundatio

### ✅ Great For

- **Microservices** with distributed caching, queuing, and messaging needs
- **Background processing** with reliable job execution
- **Event-driven architectures** using pub/sub patterns
- **Cloud applications** needing portable abstractions
- **Development teams** wanting consistent patterns across projects
- **Testing scenarios** requiring isolated, fast-running tests

### ⚠️ Consider Alternatives For

- **Simple applications** with no distributed requirements
- **Projects already invested** in specific vendor SDKs
- **Extremely high-throughput scenarios** where direct SDK access is needed

## Next Steps

Ready to get started? Here's what to explore next:

- [Getting Started](./getting-started) - Install and configure Foundatio
- [Caching](./caching) - Deep dive into caching
- [Why Choose Foundatio?](./why-foundatio) - Detailed comparison and benefits
