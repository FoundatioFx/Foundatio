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
  - icon: 📡
    title: Messaging
    details: Commands and events with handler classes, retries, dead letters and durable subscriptions on InMemory, Redis, AWS SQS/SNS, Kafka, Azure Service Bus and RabbitMQ.
  - icon: 🔒
    title: Locks
    details: Distributed locking with cache-based and throttling implementations for cross-machine coordination.
  - icon: 💼
    title: Durable Jobs
    details: Stored background jobs with arguments, progress, cancellation, retries and CRON schedules, on InMemory or Redis.
  - icon: 📬
    title: Queues and Message Bus
    details: The original IQueue<T>, IMessageBus and hosted jobs, still supported, with InMemory, Redis, Azure, AWS, Kafka and RabbitMQ implementations.
  - icon: 📁
    title: File Storage
    details: Abstracted file storage with InMemory, Folder, Azure Blob, S3, Redis, and more.
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

## Quick Examples

### Caching

```csharp
using Foundatio.Caching;

ICacheClient cache = new InMemoryCacheClient();
await cache.SetAsync("test", 1);
var value = await cache.GetAsync<int>("test");
```

[Learn more about Caching →](./guide/caching)

### Messaging

```csharp
using Foundatio.Messaging;

builder.Services.AddFoundatioMessaging(m => m
    .UseInMemory()                       // or UseRedis(), UseSqs(), UseKafka(...), UseAzureServiceBus(...), UseRabbitMQ(...)
    .AddConsumer<SendReceiptHandler>());

public record SendReceipt(int OrderId);

public class SendReceiptHandler : IMessageHandler<SendReceipt>
{
    // Retried when it throws, dead-lettered when the retries run out
    public Task HandleAsync(SendReceipt message, IMessageContext context, CancellationToken ct) => Task.CompletedTask;
}

// Anywhere, with IMessageClient from DI
await messages.SendAsync(new SendReceipt(42));
```

[Learn more about Messaging →](./guide/messaging)

### Durable Jobs

```csharp
using Foundatio.Jobs;

builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()                       // or UseRedis()
    .AddJob<GenerateReportJob>());

public record GenerateReport(string Format);

public class GenerateReportJob : IJobHandler<GenerateReport>
{
    public Task HandleAsync(GenerateReport args, IJobContext context, CancellationToken ct)
        => context.ReportProgressAsync(100, $"Generated {args.Format}", ct);
}

// Anywhere, with IJobClient from DI
var job = await jobs.EnqueueAsync(new GenerateReport("pdf"));
```

[Learn more about Durable Jobs →](./guide/durable-jobs)

### Locks

```csharp
using Foundatio.Lock;

ILockProvider locker = new CacheLockProvider(
    new InMemoryCacheClient(),
    new InMemoryMessageBus()
);

await using var lck = await locker.AcquireAsync("resource");
if (lck is null)
    throw new InvalidOperationException("Could not acquire lock");

// Do exclusive work
await ProcessAsync();
```

[Learn more about Locks →](./guide/locks)

### Queues and Message Bus (legacy)

The original `IQueue<T>` and `IMessageBus` remain supported:

```csharp
IQueue<SimpleWorkItem> queue = new InMemoryQueue<SimpleWorkItem>();
await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

IMessageBus messageBus = new InMemoryMessageBus();
await messageBus.SubscribeAsync<SimpleMessage>(msg => { /* Got message */ });
await messageBus.PublishAsync(new SimpleMessage { Data = "Hello" });
```

[Queues (legacy) →](./guide/queues) · [Message Bus (legacy) →](./guide/message-bus) · [Migrating →](./guide/migrating-to-messaging)

### File Storage

```csharp
using Foundatio.Storage;

IFileStorage storage = new InMemoryFileStorage();
await storage.SaveFileAsync("test.txt", "test");
string content = await storage.GetFileContentsAsync("test.txt");
```

[Learn more about File Storage →](./guide/storage)

### Resilience

```csharp
using Foundatio.Resilience;

var policy = new ResiliencePolicyBuilder()
    .WithMaxAttempts(3)
    .WithExponentialDelay(TimeSpan.FromSeconds(1))
    .Build();

await policy.ExecuteAsync(async ct => {
    await SomeUnreliableOperationAsync(ct);
});
```

[Learn more about Resilience →](./guide/resilience)

## Why Foundatio?

When building several large cloud applications we found a lack of great solutions for many key pieces to building scalable distributed applications while keeping the development experience simple. Here's why we built and use Foundatio:

- **Abstract Interfaces**: Build against abstract interfaces so you can easily change implementations
- **DI Friendly**: All blocks are dependency injection friendly
- **Local Development**: In-memory implementations mean no external dependencies during development
- **Swappable**: Easily swap between in-memory (development) and production implementations (Redis, Azure, AWS)
- **Battle Tested**: Used in production at [Exceptionless](https://github.com/exceptionless/Exceptionless) and other large-scale applications
- **Open Source**: Released under the permissive [Apache 2.0 License](https://github.com/FoundatioFx/Foundatio/blob/main/LICENSE.txt)
- **Actively Maintained**: Continuously developed and improved since 2015 (10+ years of production use)

## Implementations

| Provider | Caching | Queues | Messaging | Locks | Storage |
|----------|---------|--------|-----------|-------|---------|
| [Aliyun](./guide/implementations/aliyun) | | | | | ✅ |
| [AWS](./guide/implementations/aws) | | ✅ | ✅ | | ✅ |
| [Azure ServiceBus](./guide/implementations/azure) | | ✅ | ✅ | | |
| [Azure Storage](./guide/implementations/azure) | | ✅ | | | ✅ |
| [In-Memory](./guide/implementations/in-memory) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Kafka](./guide/implementations/kafka) | | | ✅ | | |
| [RabbitMQ](./guide/implementations/rabbitmq) | | | ✅ | | |
| [Redis](./guide/implementations/redis) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [SshNet](./guide/implementations/sshnet) | | | | | ✅ |

## Related Projects

- [**Foundatio.CommandQuery**](https://github.com/FoundatioFx/Foundatio.CommandQuery) - CQRS framework with Entity Framework Core and MongoDB support, built on Foundatio.Mediator.
- [**Foundatio.Lucene**](https://lucene.foundatio.dev) - Lucene-style query parser with AST, visitor pattern, Entity Framework Core integration, and Elasticsearch Query DSL generation.
- [**Foundatio.Mediator**](https://mediator.foundatio.dev) - Blazingly fast, convention-based C# mediator powered by source generators and interceptors. Near-direct call performance with zero runtime reflection.
- [**Foundatio.Parsers**](https://parsers.foundatio.dev) - Extensible Lucene-style query syntax parser with Elasticsearch integration, field aliases, query includes, and validation.
- [**Foundatio.Repositories**](https://repositories.foundatio.dev) - Production-grade repository pattern implementation with Elasticsearch support, caching, messaging, soft deletes, and document versioning.
