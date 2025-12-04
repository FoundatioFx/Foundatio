---
layout: home

hero:
  name: Foundatio
  text: Building Blocks for Distributed Apps
  tagline: Pluggable foundation blocks for building loosely coupled distributed applications
  image:
    src: https://raw.githubusercontent.com/FoundatioFx/Foundatio/main/media/foundatio-icon.png
    alt: Foundatio
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
    - theme: alt
      text: View on GitHub
      link: https://github.com/FoundatioFx/Foundatio

features:
  - icon: 🚀
    title: Caching
    details: Fast, consistent caching with InMemory, Redis, and Hybrid implementations. Includes scoped cache support.
  - icon: 📬
    title: Queues
    details: FIFO message delivery with InMemory, Redis, Azure Service Bus, Azure Storage, and AWS SQS implementations.
  - icon: 🔒
    title: Locks
    details: Distributed locking with cache-based and throttling implementations for cross-machine coordination.
  - icon: 📡
    title: Messaging
    details: Pub/sub messaging with InMemory, Redis, RabbitMQ, Kafka, and Azure Service Bus implementations.
  - icon: 💼
    title: Jobs
    details: Long-running process management with queue processor jobs and work item handlers.
  - icon: 📁
    title: File Storage
    details: Abstracted file storage with InMemory, Folder, Azure Blob, S3, Redis, Minio, and more.
  - icon: 🛡️
    title: Resilience
    details: Retry policies, circuit breakers, timeouts, and exponential backoff for robust applications.
  - icon: 💉
    title: Dependency Injection
    details: Built-in support for Microsoft.Extensions.DependencyInjection with easy service registration.
  - icon: 🔄
    title: Swappable Implementations
    details: Abstract interfaces allow easy swapping between in-memory (dev) and production implementations.
  - icon: 🧪
    title: Testability
    details: In-memory implementations make testing easy without external dependencies.
  - icon: 🔧
    title: Extensibility
    details: Modular design allows adding custom implementations for any abstraction.
  - icon: 📊
    title: Metrics & Logging
    details: Optional metrics and logging integration for observability.
---

## Quick Example

### Caching

```csharp
using Foundatio.Caching;

ICacheClient cache = new InMemoryCacheClient();
await cache.SetAsync("test", 1);
var value = await cache.GetAsync<int>("test");
```

### Queues

```csharp
using Foundatio.Queues;

IQueue<SimpleWorkItem> queue = new InMemoryQueue<SimpleWorkItem>();

await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
var workItem = await queue.DequeueAsync();
```

### Locks

```csharp
using Foundatio.Lock;

ILockProvider locker = new CacheLockProvider(
    new InMemoryCacheClient(),
    new InMemoryMessageBus()
);
var testLock = await locker.AcquireAsync("test");
// ... do work
await testLock.ReleaseAsync();
```

### Messaging

```csharp
using Foundatio.Messaging;

IMessageBus messageBus = new InMemoryMessageBus();
await messageBus.SubscribeAsync<SimpleMessage>(msg => {
  // Got message
});

await messageBus.PublishAsync(new SimpleMessage { Data = "Hello" });
```

### File Storage

```csharp
using Foundatio.Storage;

IFileStorage storage = new InMemoryFileStorage();
await storage.SaveFileAsync("test.txt", "test");
string content = await storage.GetFileContentsAsync("test.txt");
```

## Why Foundatio?

When building several large cloud applications we found a lack of great solutions for many key pieces to building scalable distributed applications while keeping the development experience simple. Here's why we built and use Foundatio:

- **Abstract Interfaces**: Build against abstract interfaces so you can easily change implementations
- **DI Friendly**: All blocks are dependency injection friendly
- **Local Development**: In-memory implementations mean no external dependencies during development
- **Swappable**: Easily swap between in-memory (development) and production implementations (Redis, Azure, AWS)
- **Battle Tested**: Used in production at [Exceptionless](https://github.com/exceptionless/Exceptionless) and other large-scale applications

## Implementations

| Provider | Caching | Queues | Messaging | Locks | Storage |
|----------|---------|--------|-----------|-------|---------|
| [In-Memory](./guide/implementations/in-memory) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Redis](https://github.com/FoundatioFx/Foundatio.Redis) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Azure Storage](https://github.com/FoundatioFx/Foundatio.AzureStorage) | | ✅ | | | ✅ |
| [Azure ServiceBus](https://github.com/FoundatioFx/Foundatio.AzureServiceBus) | | ✅ | ✅ | | |
| [AWS](https://github.com/FoundatioFx/Foundatio.AWS) | | ✅ | | | ✅ |
| [Kafka](https://github.com/FoundatioFx/Foundatio.Kafka) | | | ✅ | | |
| [RabbitMQ](https://github.com/FoundatioFx/Foundatio.RabbitMQ) | | | ✅ | | |
| [Minio](https://github.com/FoundatioFx/Foundatio.Minio) | | | | | ✅ |
| [Aliyun](https://github.com/FoundatioFx/Foundatio.Aliyun) | | | | | ✅ |
| [SshNet](https://github.com/FoundatioFx/Foundatio.Storage.SshNet) | | | | | ✅ |

## Related Projects

- [**Foundatio.Mediator**](https://github.com/FoundatioFx/Foundatio.Mediator) - Blazingly fast, convention-based C# mediator powered by source generators and interceptors. Near-direct call performance with zero runtime reflection.
