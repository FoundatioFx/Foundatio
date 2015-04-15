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
ICacheClient cache = new InMemoryCacheClient();
cache.Set("test", 1);
var value = cache.Get<int>("test");
```

### Queues

#### Sample

```csharp
IQueue<SimpleWorkItem> queue = new InMemoryQueue<SimpleWorkItem>();

queue.Enqueue(new SimpleWorkItem {
    Data = "Hello"
});

var workItem = queue.Dequeue(TimeSpan.Zero);
```

### Locks

#### Sample

```csharp
ILockProvider locker = new CacheLockProvider(new InMemoryCacheClient());

using (locker) {
  locker.ReleaseLock("test");

  using (locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
    // ...
  }
}
```

### Messaging

#### Sample

```csharp
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

### Jobs

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
