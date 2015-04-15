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

## Getting Started (Development)

[Foundatio can be installed](https://www.nuget.org/packages?q=Foundatio) via the [NuGet package manager](https://docs.nuget.org/consume/Package-Manager-Dialog). If you need help, please contact us via in-app support or [open an issue](https://github.com/exceptionless/Foundatio/issues/new). We’re always here to help if you have any questions!

1. You will need to have [Visual Studio 2013](http://www.visualstudio.com/products/visual-studio-community-vs) installed.
2. Open the `Foundatio.sln` Visual Studio solution file.

## Using Foundatio
The sections below contain a small subset of what's possible with Foundatio. We recommend taking a peek at the source code for more information. Please let us know if you have any questions or need assistance!

### [Caching](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Caching)

Caching allows you to store and access data lightning fast, saving you exspensive operations to create or get data. We provide four different cache implementations that derive from the [`ICacheClient` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/ICacheClient.cs):

1. [InMemoryCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/InMemoryCacheClient.cs): An in memory cache client implementation. This cache implementation is only valid for the lifetime of the process.
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

1. [InMemoryMessageBus](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Messaging/InMemoryMessageBus.cs): An in message bus implementation. This message bus implementation is only valid for the lifetime of the process.
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

### File Storage

#### Sample

```csharp
IFileStorage storage = new InMemoryFileStorage();
storage.SaveFile("test.txt", "test");
string content = storage.GetFileContents("test.txt")
```

### Metrics

#### Sample

```csharp
metrics.Counter("c1");
metrics.Gauge("g1", 2.534);
metrics.Timer("t1", 50788);
metrics.DisplayStats(TextWriter);
```

## Roadmap

This is a list of high level things that we are planning to do:
- Async Support **(In Progress: Some of our implementations are already fully Async)** 
- Long Running Jobs **(In Progress)** 
- vnext support
- [Let us know what you'd like us to work on!](https://github.com/exceptionless/Foundatio/issues)

##Thanks
Thanks to the community for your support!

Thanks to [JetBrains](http://jetbrains.com) for a community [WebStorm](https://www.jetbrains.com/webstorm/) and [ReSharper](https://www.jetbrains.com/resharper/) license to use on this project. It's the best JavaScript IDE/Visual Studio productivity enhancement hands down.

Thanks to [Red Gate](http://www.red-gate.com) for providing an open source license for a [.NET Developer Bundle](http://www.red-gate.com/products/dotnet-development/). It's an indepensible tool when you need to track down a performance/memory issue.
