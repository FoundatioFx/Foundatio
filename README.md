![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio-dark-bg.svg#gh-dark-mode-only "Foundatio")![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.svg#gh-light-mode-only "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.svg?style=flat)](https://www.nuget.org/packages/Foundatio/)
[![feedz.io](https://img.shields.io/badge/endpoint.svg?url=https%3A%2F%2Ff.feedz.io%2Ffoundatio%2Ffoundatio%2Fshield%2FFoundatio%2Flatest)](https://f.feedz.io/foundatio/foundatio/packages/Foundatio/latest/download)
[![Discord](https://img.shields.io/discord/715744504891703319)](https://discord.gg/6HxgFCx)

Pluggable foundation blocks for building loosely coupled distributed apps.

## ✨ Why Choose Foundatio?

- 🔌 **Pluggable implementations** - Swap Redis, Azure, AWS, or in-memory with no code changes
- 🧪 **Developer friendly** - In-memory implementations for fast local development and testing
- 💉 **DI native** - Built for Microsoft.Extensions.DependencyInjection
- 🎯 **Interface-first** - Code against abstractions, not implementations
- ⚡ **Production ready** - Battle-tested in high-scale applications
- 🔄 **Consistent APIs** - Same patterns across caching, queues, storage, and more

## 🧱 Core Building Blocks

| Feature | Description |
|---------|-------------|
| [**Caching**](https://foundatio.dev/guide/caching) | In-memory, Redis, and hybrid caching with automatic invalidation |
| [**Queues**](https://foundatio.dev/guide/queues) | Reliable message queuing with Redis, Azure, AWS SQS |
| [**Locks**](https://foundatio.dev/guide/locks) | Distributed locking and throttling |
| [**Messaging**](https://foundatio.dev/guide/messaging) | Pub/sub with Redis, RabbitMQ, Kafka, Azure Service Bus |
| [**Jobs**](https://foundatio.dev/guide/jobs) | Background job processing with queue integration |
| [**File Storage**](https://foundatio.dev/guide/storage) | Unified file API for disk, S3, Azure Blob, and more |
| [**Resilience**](https://foundatio.dev/guide/resilience) | Retry policies, circuit breakers, and timeouts |

## 🚀 Quick Start

```bash
dotnet add package Foundatio
```

## Implementations

- [Redis](https://github.com/FoundatioFx/Foundatio.Redis) - Caching, Storage, Queues, Messaging, Locks
- [Azure Storage](https://github.com/FoundatioFx/Foundatio.AzureStorage) - Storage, Queues
- [Azure ServiceBus](https://github.com/FoundatioFx/Foundatio.AzureServiceBus) - Queues, Messaging
- [AWS](https://github.com/FoundatioFx/Foundatio.AWS) - Storage, Queues
- [Kafka](https://github.com/FoundatioFx/Foundatio.Kafka) - Messaging
- [RabbitMQ](https://github.com/FoundatioFx/Foundatio.RabbitMQ) - Messaging
- [Minio](https://github.com/FoundatioFx/Foundatio.Minio) - Storage
- [Aliyun](https://github.com/FoundatioFx/Foundatio.Aliyun) - Storage
- [SshNet](https://github.com/FoundatioFx/Foundatio.Storage.SshNet) - Storage

## Getting Started (Development)

[Foundatio can be installed](https://www.nuget.org/packages?q=Foundatio) via the [NuGet package manager](https://docs.nuget.org/consume/Package-Manager-Dialog). If you need help, please [open an issue](https://github.com/FoundatioFx/Foundatio/issues/new) or join our [Discord](https://discord.gg/6HxgFCx) chat room. We’re always here to help if you have any questions!

**This section is for development purposes only! If you are trying to use the Foundatio libraries, please get them from NuGet.**

1. You will need to have [Visual Studio Code](https://code.visualstudio.com) installed.
2. Open the `Foundatio.slnx` Visual Studio solution file.

## Using Foundatio

The sections below contain a small subset of what's possible with Foundatio. We recommend taking a peek at the source code for more information. Please let us know if you have any questions or need assistance!

### [Caching](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Caching)

Caching allows you to store and access data lightning fast, saving you exspensive operations to create or get data. We provide four different cache implementations that derive from the [`ICacheClient` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/ICacheClient.cs):

1. [InMemoryCacheClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/InMemoryCacheClient.cs): An in memory cache client implementation. This cache implementation is only valid for the lifetime of the process. It's worth noting that the in memory cache client has the ability to cache the last X items via the `MaxItems` property or limit cache size via `MaxMemorySize` (in bytes) with intelligent size-aware eviction. We use this in [Exceptionless](https://github.com/exceptionless/Exceptionless) to only [keep the last 250 resolved geoip results](https://github.com/exceptionless/Exceptionless/blob/master/src/Exceptionless.Core/Geo/MaxMindGeoIpService.cs).
2. [HybridCacheClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/HybridCacheClient.cs): This cache implementation uses both an `ICacheClient` and the `InMemoryCacheClient` and uses an `IMessageBus` to keep the cache in sync across processes. This can lead to **huge wins in performance** as you are saving a serialization operation and a call to the remote cache if the item exists in the local cache.
3. [RedisCacheClient](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Cache/RedisCacheClient.cs): A Redis cache client implementation.
4. [RedisHybridCacheClient](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Cache/RedisHybridCacheClient.cs): An implementation of `HybridCacheClient` that uses the `RedisCacheClient` as `ICacheClient` and the `RedisMessageBus` as `IMessageBus`.
5. [ScopedCacheClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/ScopedCacheClient.cs): This cache implementation takes an instance of `ICacheClient` and a string `scope`. The scope is prefixed onto every cache key. This makes it really easy to scope all cache keys and remove them with ease.

#### Sample
```csharp
// Caching
ICacheClient cache = new InMemoryCacheClient();
await cache.SetAsync("user:123", user, TimeSpan.FromMinutes(5));
var cached = await cache.GetAsync<User>("user:123");

#### Memory-Limited Cache

The `InMemoryCacheClient` supports memory-based eviction with intelligent size-aware cleanup. When the cache exceeds the memory limit, it evicts items based on a combination of size, age, and access recency.

```csharp
using Foundatio.Caching;

// Use dynamic sizing for automatic size calculation (recommended for mixed object types)
var cache = new InMemoryCacheClient(o => o
    .WithDynamicSizing(maxMemorySize: 100 * 1024 * 1024) // 100 MB limit
    .MaxItems(10000)); // Optional: also limit by item count

// Use fixed sizing for maximum performance (when objects are uniform)
var fixedSizeCache = new InMemoryCacheClient(o => o
    .WithFixedSizing(
        maxMemorySize: 50 * 1024 * 1024,  // 50 MB limit
        averageObjectSize: 1024));         // Assume 1KB per object

// Check current memory usage
Console.WriteLine($"Memory: {cache.CurrentMemorySize:N0} / {cache.MaxMemorySize:N0} bytes");
```

### [Queues](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Queues)
// Queuing
IQueue<WorkItem> queue = new InMemoryQueue<WorkItem>();
await queue.EnqueueAsync(new WorkItem { Data = "Hello" });
var entry = await queue.DequeueAsync();

// File Storage
IFileStorage storage = new InMemoryFileStorage();
await storage.SaveFileAsync("docs/readme.txt", "Hello World");

// Distributed Locks
ILockProvider locker = new CacheLockProvider(cache, messageBus);
await using var handle = await locker.AcquireAsync("resource-key");
```

## 📦 Implementations

| Provider | Caching | Queues | Messaging | Storage | Locks |
|----------|---------|--------|-----------|---------|-------|
| [In-Memory](https://foundatio.dev/guide/implementations/in-memory) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Redis](https://github.com/FoundatioFx/Foundatio.Redis) | ✅ | ✅ | ✅ | ✅ | ✅ |
| [Azure Storage](https://github.com/FoundatioFx/Foundatio.AzureStorage) | | ✅ | | ✅ | |
| [Azure Service Bus](https://github.com/FoundatioFx/Foundatio.AzureServiceBus) | | ✅ | ✅ | | |
| [AWS (S3/SQS)](https://github.com/FoundatioFx/Foundatio.AWS) | | ✅ | | ✅ | |
| [RabbitMQ](https://github.com/FoundatioFx/Foundatio.RabbitMQ) | | | ✅ | | |
| [Kafka](https://github.com/FoundatioFx/Foundatio.Kafka) | | | ✅ | | |
| [Minio](https://github.com/FoundatioFx/Foundatio.Minio) | | | | ✅ | |
| [Aliyun](https://github.com/FoundatioFx/Foundatio.Aliyun) | | | | ✅ | |
| [SFTP](https://github.com/FoundatioFx/Foundatio.Storage.SshNet) | | | | ✅ | |

## 📚 Learn More

**👉 [Complete Documentation](https://foundatio.dev)**

Key topics:

- [Getting Started](https://foundatio.dev/guide/getting-started) - Installation and setup
- [Caching](https://foundatio.dev/guide/caching) - Cache implementations and patterns
- [Queues](https://foundatio.dev/guide/queues) - Message queue processing
- [Jobs](https://foundatio.dev/guide/jobs) - Background job execution
- [Configuration](https://foundatio.dev/guide/configuration) - Options and settings

## 📦 CI Packages (Feedz)

Want the latest CI build before it hits NuGet? Add the Feedz source and install the pre-release version:

```bash
dotnet nuget add source https://f.feedz.io/foundatio/foundatio/nuget -n foundatio-feedz
dotnet add package Foundatio --prerelease
```

Or add to your `NuGet.config`:

```xml
<configuration>
  <packageSources>
    <add key="foundatio-feedz" value="https://f.feedz.io/foundatio/foundatio/nuget" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="foundatio-feedz">
      <package pattern="Foundatio.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request. See our [documentation](https://foundatio.dev) for development guidelines.

**Development Setup:**

1. Clone the repository
2. Open `Foundatio.slnx` in Visual Studio or VS Code
3. Run `dotnet build` to build
4. Run `dotnet test` to run tests

## 🔗 Related Projects

- [**Foundatio.Mediator**](https://github.com/FoundatioFx/Foundatio.Mediator) - Blazingly fast, convention-based C# mediator powered by source generators

## 📄 License

Apache 2.0 License

## Thanks to all the people who have contributed

[![contributors](https://contributors-img.web.app/image?repo=foundatiofx/foundatio)](https://github.com/foundatiofx/foundatio/graphs/contributors)
