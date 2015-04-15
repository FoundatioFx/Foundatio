# Foundatio
[![Build status](https://ci.appveyor.com/api/projects/status/mpak90b87dl9crl8?svg=true)](https://ci.appveyor.com/project/Exceptionless/foundatio)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.svg?style=flat)](https://www.nuget.org/packages/Foundatio/) [![NuGet Downloads](http://img.shields.io/nuget/dt/Foundatio.svg?style=flat)](https://www.nuget.org/packages/Foundatio/) [![Gitter](https://badges.gitter.im/Join Chat.svg)](https://gitter.im/exceptionless/Discuss)

Pluggable foundation blocks for building distributed apps.
- Caching
- Queues
- Locks
- Messaging
- Jobs
- File Storage
- Metrics

Includes implementations in Redis, Azure and in memory (for development).

## Why should I use Foundatio?
When we first started building [Exceptionless](https://github.com/exceptionless/Exceptionless) we found a lack of great solutions (that's not to say there isn't great solutions out there) for many key peices to building scalable distributed applications while keeping cost of development and testing a zero sum. Here is a few examples of why we built and use Foundatio:
 * Caching: We were initially using an open source redis cache client but then it turned into a commercial product with high licensing costs. Not only that, but there wasn't any in memory implementations so every developer was required to setup and configure redis.
 * MessageBus: We initially looked at [NServiceBus](http://particular.net/nservicebus) (great product) but it had a high licensing costs (they have to eat too) but was not oss friendly. We also looked into [MassTransit](http://masstransit-project.com/) but found azure support lacking and local setup a pain. We wanted a simple message bus that just worked locally or in the cloud.
 * Storage: We couldn't find any existing projects that was decoupled and supported in memory, file storage or Azure Blob Storage.

To summarize, if you want pain free development and testing while allowing your app to scale, use Foundatio!

## Getting Started (Development)

[Foundatio can be installed](https://www.nuget.org/packages?q=Foundatio) via the [NuGet package manager](https://docs.nuget.org/consume/Package-Manager-Dialog). If you need help, please contact us via in-app support or [open an issue](https://github.com/exceptionless/Foundatio/issues/new). We’re always here to help if you have any questions!

1. You will need to have [Visual Studio 2013](http://www.visualstudio.com/products/visual-studio-community-vs) installed.
2. Open the `Foundatio.sln` Visual Studio solution file.

## Using Foundatio
The sections below contain a small subset of what's possible with Foundatio. We recommend taking a peek at the source code for more information. Please let us know if you have any questions or need assistance!

### [Caching](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Caching)

Caching allows you to store and access data lightning fast, saving you exspensive operations to create or get data. We provide four different cache implementations that derive from the [`ICacheClient` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/ICacheClient.cs):

1. [InMemoryCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/InMemoryCacheClient.cs): An in memory cache client implementation. This cache implementation is only valid for the lifetime of the process. It's worth noting that the in memory cache client has the ability to cache the last X items via the `MaxItems` property. We use this in [Exceptionless](https://github.com/exceptionless/Exceptionless) to only [keep the last 250 resolved geoip results](https://github.com/exceptionless/Exceptionless/blob/master/Source/Core/Geo/MindMaxGeoIPResolver.cs).
2. [HybridCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/HybridCacheClient.cs): This cache implementation uses the `InMemoryCacheClient` and uses the `IMessageBus` to keep the cache in sync across processes.
3. [RedisCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Cache/RedisCacheClient.cs): An redis cache client implementation.
4. [RedisHybridCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Cache/RedisHybridCacheClient.cs): This cache implementation uses both the `RedisCacheClient` and `InMemoryCacheClient` implementations and uses the `RedisMessageBus` to keep the in memory cache in sync across processes. This can lead to **huge wins in performance** as you are saving a serialization operation and call to redis if the item exists in the local cache.

We recommend using all of the `ICacheClient` implementations as singletons. 

#### Sample

```csharp
using Foundatio.Caching;

ICacheClient cache = new InMemoryCacheClient();
cache.Set("test", 1);
var value = cache.Get<int>("test");
```

### [Queues](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Queues)

Queues offer First In, First Out (FIFO) message delivery. We provide three different queue implementations that derive from the [`IQueue` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Queues/IQueue.cs):

1. [InMemoryQueue](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Queues/InMemoryQueue.cs): An in memory queue implementation. This queue implementation is only valid for the lifetime of the process.
2. [RedisQueue](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Queues/RedisQueue.cs): An redis queue implementation.
3. [ServiceBusQueue](https://github.com/exceptionless/Foundatio/blob/master/src/Azure/Queues/ServiceBusQueue.cs): An Azure Service Bus Queue implementation.

We recommend using all of the `IQueue` implementations as singletons. 

#### Sample

```csharp
using Foundatio.Queues;

IQueue<SimpleWorkItem> queue = new InMemoryQueue<SimpleWorkItem>();

queue.Enqueue(new SimpleWorkItem {
    Data = "Hello"
});

var workItem = queue.Dequeue(TimeSpan.Zero);
```

### [Locks](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Lock)

Locks ensure a resource is only accessed by one consumer at any given time. We provide two different locking implementations that derive from the [`ILockProvider` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Lock/ILockProvider.cs):

1. [CacheLockProvider](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Lock/CacheLockProvider.cs): A basic lock implementation.
2. [ThrottlingLockProvider](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Lock/ThrottlingLockProvider.cs): A lock implementation that only allows a certian amount of locks through. You could use this to throttle api calls to some external service and it will throttle them across all processes asking for that lock.

It's worth noting that all lock providers take a `ICacheClient`. This allows you to ensure your code locks properly across machines. We recommend using all of the `ILockProvider` implementations as singletons. 

#### Sample

```csharp
using Foundatio.Lock;

ILockProvider locker = new CacheLockProvider(new InMemoryCacheClient());

using (locker) {
  locker.ReleaseLock("test");

  using (locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
    // ...
  }
}
```

### [Messaging](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Messaging)

Allows you to do publish/subscribe to messages flowing through your application.  We provide three different message bus implementations that derive from the [`IMessageBus` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Messaging/IMessageBus.cs):

1. [InMemoryMessageBus](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Messaging/InMemoryMessageBus.cs): An in memory message bus implementation. This message bus implementation is only valid for the lifetime of the process.
2. [RedisMessageBus](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Messaging/RedisMessageBus.cs): An redis message bus implementation.
3. [ServiceBusMessageBus](https://github.com/exceptionless/Foundatio/blob/master/src/Azure/Messaging/ServiceBusMessageBus.cs): An Azure Service Bus implementation.

We recommend using all of the `IMessageBus` implementations as singletons. 

#### Sample

```csharp
using Foundatio.Messaging;

IMessageBus messageBus = new InMemoryMessageBus();

using (messageBus) {
  messageBus.Subscribe<SimpleMessageA>(msg => {
    // Got message
  });
  
  messageBus.Publish(new SimpleMessageA {
      Data = "Hello"
  });
}
```

### [Jobs](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Jobs)

All jobs must derive from the  [`JobBase` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/JobBase.cs). You can then run jobs by calling `Run()` on the job or passing it to the [`JobRunner` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/JobRunner.cs).

#### Sample

```csharp
using Foundatio.Jobs;

public class HelloWorldJob : JobBase {
  public int RunCount { get; set; }

  protected override Task<JobResult> RunInternalAsync(CancellationToken token) {
    RunCount++;

    return Task.FromResult(JobResult.Success);
  }
}

var job = new HelloWorldJob();
job.Run(); // job.RunCount = 1;
job.RunContinuous(iterationLimit: 2); // job.RunCount = 3;
job.RunContinuous(token: new CancellationTokenSource(TimeSpan.FromMilliseconds(10)).Token); // job.RunCount > 10;
```

### [File Storage](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Storage)

We provide three different file storage implementations that derive from the [`IFileStorage` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Storage/IFileStorage.cs):

1. [InMemoryFileStorage](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Storage/InMemoryFileStorage.cs): An in memory file implementation. This file storage implementation is only valid for the lifetime of the process.
2. [FolderFileStorage](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Storage/FolderFileStorage.cs): An file storage implementation that uses the hard drive for storage.
3. [AzureFileStorage](https://github.com/exceptionless/Foundatio/blob/master/src/AzureStorage/Storage/AzureFileStorage.cs): An Azure Blob Storage implementation.

We recommend using all of the `IFileStorage` implementations as singletons. 

#### Sample

```csharp
using Foundatio.Storage;

IFileStorage storage = new InMemoryFileStorage();
storage.SaveFile("test.txt", "test");
string content = storage.GetFileContents("test.txt")
```

### [Metrics](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Metrics)

We provide two different metric implementations that derive from the [`IMetricsClient` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Metrics/IMetricsClient.cs):

1. [InMemoryMetricsClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Metrics/InMemoryMetricsClient.cs): An in memory metrics implementation. This metrics implementation is only valid for the lifetime of the process. It's worth noting that this metrics client also has the ability to display the metrics to a `TextWriter` on a timer or by calling `DisplayStats(TextWriter)`.
  ```
 Counter: c1 Value: 1            Rate: 48.89/s     Rate: 47.97/s
    Gauge: g1 Value: 2.53          Avg: 2.53         Max: 2.53
    Timing: t1   Min: 50,788ms      Avg: 50,788ms     Max: 50,788ms
  ```

2. [StatsDMetricsClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Metrics/StatsDMetricsClient.cs): An statsd metrics implementation.

We recommend using all of the `IMetricsClient` implementations as singletons. 

#### Sample

```csharp
metrics.Counter("c1");
metrics.Gauge("g1", 2.534);
metrics.Timer("t1", 50788);
```

## Roadmap

This is a list of high level things that we are planning to do:
- Async Support **(In Progress: Some of our implementations are already fully Async)** 
- Long Running Jobs **(In Progress)** 
- vnext support
- [Let us know what you'd like us to work on!](https://github.com/exceptionless/Foundatio/issues)
