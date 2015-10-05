# Foundatio
[![Build status](https://ci.appveyor.com/api/projects/status/mpak90b87dl9crl8?svg=true)](https://ci.appveyor.com/project/Exceptionless/foundatio)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.svg?style=flat)](https://www.nuget.org/packages/Foundatio/) [![NuGet Downloads](http://img.shields.io/nuget/dt/Foundatio.svg?style=flat)](https://www.nuget.org/packages/Foundatio/) [![Gitter](https://badges.gitter.im/Join Chat.svg)](https://gitter.im/exceptionless/Discuss)

Pluggable foundation blocks for building loosely coupled distributed apps.
- Caching
- Queues
- Locks
- Messaging
- Jobs
- File Storage
- Metrics
- Logging

Includes implementations in Redis, Azure and in memory (for development).

## Why should I use Foundatio?
When we first started building [Exceptionless](https://github.com/exceptionless/Exceptionless) we found a lack of great solutions (that's not to say there isn't great solutions out there) for many key pieces to building scalable distributed applications while keeping the development experience simple. Here are a few examples of why we built and use Foundatio:
 * Wanted to build against abstract interfaces so that we could easily change implementations.
 * Wanted the blocks to be dependency injection friendly.
 * Caching: We were initially using an open source Redis cache client but then it turned into a commercial product with high licensing costs. Not only that, but there wasn't any in memory implementations so every developer was required to set up and configure Redis.
 * Message Bus: We initially looked at [NServiceBus](http://particular.net/nservicebus) (great product) but it had high licensing costs (they have to eat too) but was not OSS friendly. We also looked into [MassTransit](http://masstransit-project.com/) but found Azure support lacking and local set up a pain. We wanted a simple message bus that just worked locally or in the cloud.
 * Storage: We couldn't find any existing project that was decoupled and supported in memory, file storage or Azure Blob Storage.

To summarize, if you want pain free development and testing while allowing your app to scale, use Foundatio!

## Getting Started (Development)

[Foundatio can be installed](https://www.nuget.org/packages?q=Foundatio) via the [NuGet package manager](https://docs.nuget.org/consume/Package-Manager-Dialog). If you need help, please contact us via in-app support or [open an issue](https://github.com/exceptionless/Foundatio/issues/new). We’re always here to help if you have any questions!

**This section is for development purposes only! If you are trying to use the Foundatio libraries, please get them from NuGet.**

1. You will need to have [Visual Studio 2015](http://www.visualstudio.com/products/visual-studio-community-vs) installed.
2. Open the `Foundatio.sln` Visual Studio solution file.

## Using Foundatio
The sections below contain a small subset of what's possible with Foundatio. We recommend taking a peek at the source code for more information. Please let us know if you have any questions or need assistance!

### [Caching](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Caching)

Caching allows you to store and access data lightning fast, saving you exspensive operations to create or get data. We provide four different cache implementations that derive from the [`ICacheClient` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/ICacheClient.cs):

1. [InMemoryCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/InMemoryCacheClient.cs): An in memory cache client implementation. This cache implementation is only valid for the lifetime of the process. It's worth noting that the in memory cache client has the ability to cache the last X items via the `MaxItems` property. We use this in [Exceptionless](https://github.com/exceptionless/Exceptionless) to only [keep the last 250 resolved geoip results](https://github.com/exceptionless/Exceptionless/blob/master/Source/Core/Geo/MindMaxGeoIPResolver.cs).
2. [HybridCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/HybridCacheClient.cs): This cache implementation uses the `InMemoryCacheClient` and uses the `IMessageBus` to keep the cache in sync across processes.
3. [RedisCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Cache/RedisCacheClient.cs): A Redis cache client implementation.
4. [RedisHybridCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Cache/RedisHybridCacheClient.cs): This cache implementation uses both the `RedisCacheClient` and `InMemoryCacheClient` implementations and uses the `RedisMessageBus` to keep the in memory cache in sync across processes. This can lead to **huge wins in performance** as you are saving a serialization operation and call to Redis if the item exists in the local cache.
5. [ScopedCacheClient](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Caching/ScopedCacheClient.cs): This cache implementation takes an instance of `ICacheClient` and a string `scope`. The scope is prefixed onto every cache key. This makes it really easy to scope all cache keys and remove them with ease.

#### Sample

```csharp
using Foundatio.Caching;

ICacheClient cache = new InMemoryCacheClient();
await cache.SetAsync("test", 1);
var value = await cache.GetAsync<int>("test");
```

### [Queues](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Queues)

Queues offer First In, First Out (FIFO) message delivery. We provide three different queue implementations that derive from the [`IQueue` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Queues/IQueue.cs):

1. [InMemoryQueue](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Queues/InMemoryQueue.cs): An in memory queue implementation. This queue implementation is only valid for the lifetime of the process.
2. [RedisQueue](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Queues/RedisQueue.cs): An Redis queue implementation.
3. [ServiceBusQueue](https://github.com/exceptionless/Foundatio/blob/master/src/Azure/Queues/ServiceBusQueue.cs): An Azure Service Bus Queue implementation.

#### Sample

```csharp
using Foundatio.Queues;

IQueue<SimpleWorkItem> queue = new InMemoryQueue<SimpleWorkItem>();

await queue.EnqueueAsync(new SimpleWorkItem {
    Data = "Hello"
});

var workItem = await queue.DequeueAsync();
```

### [Locks](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Lock)

Locks ensure a resource is only accessed by one consumer at any given time. We provide two different locking implementations that derive from the [`ILockProvider` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Lock/ILockProvider.cs):

1. [CacheLockProvider](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Lock/CacheLockProvider.cs): A lock implementation that uses cache to communicate between processes.
2. [ThrottlingLockProvider](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Lock/ThrottlingLockProvider.cs): A lock implementation that only allows a certain amount of locks through. You could use this to throttle api calls to some external service and it will throttle them across all processes asking for that lock.

It's worth noting that all lock providers take a `ICacheClient`. This allows you to ensure your code locks properly across machines.

#### Sample

```csharp
using Foundatio.Lock;

ILockProvider locker = new CacheLockProvider(new InMemoryCacheClient());
using (await locker.AcquireLockAsync("test")) {
  // ...
}
```

### [Messaging](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Messaging)

Allows you to publish and subscribe to messages flowing through your application.  We provide three different message bus implementations that derive from the [`IMessageBus` interface](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Messaging/IMessageBus.cs):

1. [InMemoryMessageBus](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Messaging/InMemoryMessageBus.cs): An in memory message bus implementation. This message bus implementation is only valid for the lifetime of the process.
2. [RedisMessageBus](https://github.com/exceptionless/Foundatio/blob/master/src/Redis/Messaging/RedisMessageBus.cs): A Redis message bus implementation.
3. [ServiceBusMessageBus](https://github.com/exceptionless/Foundatio/blob/master/src/Azure/Messaging/ServiceBusMessageBus.cs): An Azure Service Bus implementation.

#### Sample

```csharp
using Foundatio.Messaging;

IMessageBus messageBus = new InMemoryMessageBus();
await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
  // Got message
});

await messageBus.PublishAsync(new SimpleMessageA { Data = "Hello" });
```

### [Jobs](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Jobs)

Allows you to run a long running process (in process or out of process) with out worrying about it being terminated prematurely. We provide a few different ways of defining a job based on your use case.

1. **Jobs**: All jobs must derive from the  [`JobBase` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/JobBase.cs). You can then run jobs by calling `RunAsync()` on the job or passing it to the [`JobRunner` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/JobRunner.cs).  The JobRunner can be used to easily run your jobs as Azure Web Jobs.

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
  ```
  
  ```csharp
  var job = new HelloWorldJob();
  await job.RunAsync(); // job.RunCount = 1;
  await job.RunContinuousAsync(iterationLimit: 2); // job.RunCount = 3;
  await job.RunContinuousAsync(cancellationToken: new CancellationTokenSource(TimeSpan.FromMilliseconds(10)).Token); // job.RunCount > 10;
  ```

  ```
  Job.exe -t "MyLib.HelloWorldJob,MyLib"
  ```

2. **Queue Processor Jobs**: A queue processor job works great for working with jobs that will be driven from queued data. Queue Processor jobs must derive from [`QueueProcessorJobBase<T>` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/QueueProcessorJobBase.cs) which also inherits from the [`JobBase` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/JobBase.cs). You can then run jobs by calling `RunAsync()` on the job or passing it to the [`JobRunner` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/JobRunner.cs).  The JobRunner can be used to easily run your jobs as Azure Web Jobs.

  #### Sample

  ```csharp
  using Foundatio.Jobs;

  public class HelloWorldQueueJob : QueueProcessorJobBase<HelloWorldQueueItem> {
    public int RunCount { get; set; }

    public HelloWorldQueueJob(IQueue<HelloWorldQueueItem> queue) : base(queue) {}
    
    protected override Task<JobResult> ProcessQueueItemAsync(QueueEntry<HelloWorldQueueItem> queueEntry, CancellationToken cancellationToken = default(CancellationToken)) {
       RunCount++;

       return Task.FromResult(JobResult.Success);
    }
  }
  
  public class HelloWorldQueueItem {
    public string Message { get; set; }
  }
  ```
  
  ```csharp
   // Register the queue for HelloWorldQueueItem. 
  container.RegisterSingleton<IQueue<HelloWorldQueueItem>>(() => new InMemoryQueue<HelloWorldQueueItem>());
  
  // To trigger the job we need to queue the HelloWorldWorkItem message. 
  // This assumes that we injected an instance of IQueue<HelloWorldWorkItem> queue
  
  var job = new HelloWorldQueueJob();
  await job.RunAsync(); // job.RunCount = 0; The RunCount wasn't incremented because we didn't enqueue any data.
  
  await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
  await job.RunAsync(); // job.RunCount = 1;
  
  await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
  await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
  await job.RunUntilEmptyAsync(); // job.RunCount = 3;
  ```

  ```
  Job.exe -t "MyLib.HelloWorldQueueJob,MyLib"
  ``` 

3. **Work Item Jobs**: A work item job will run in a job pool among other work item jobs. This type of job works great for things that don't happen often but should be in a job (Example: Deleting an entity that has many children.). It will be triggered when you publish a message on the `message bus`. The job must derive from the  [`WorkItemHandlerBase` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/WorkItemJob/WorkItemHandlers.cs). You can then run all shared jobs via [`JobRunner` class](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Jobs/JobRunner.cs).  The JobRunner can be used to easily run your jobs as Azure Web Jobs.

  #### Sample

  ```csharp
  using System.Threading.Tasks;
  using Foundatio.Jobs;

  public class HelloWorldWorkItemHandler : WorkItemHandlerBase {
    public override async Task HandleItemAsync(WorkItemContext ctx, CancellationToken cancellationToken = default(CancellationToken)) {
      var workItem = ctx.GetData<HelloWorldWorkItem>();

      // We can report the progress over the message bus easily.
      // To recieve these messages just inject IMessageSubscriber
      // and Subscribe to messages of type WorkItemStatus
      await ctx.ReportProgressAsync(0, "Starting Hello World Job");
      await Task.Delay(TimeSpan.FromSeconds(2.5));
      await ctx.ReportProgressAsync(50, String.Format("Reading value"));
      await Task.Delay(TimeSpan.FromSeconds(.5));
      await ctx.ReportProgressAsync(70, String.Format("Reading value."));
      await Task.Delay(TimeSpan.FromSeconds(.5));
      await ctx.ReportProgressAsync(90, String.Format("Reading value.."));
      await Task.Delay(TimeSpan.FromSeconds(.5));

      await ctx.ReportProgressAsync(100, workItem.Message);
    }
  }

  public class HelloWorldWorkItem {
    public string Message { get; set; }
  }
  ```
 
  ```csharp
  // Register the shared job.
  var handlers = new WorkItemHandlers();
  handlers.Register<HelloWorldWorkItem, HelloWorldWorkItemHandler>();
  
  // Register the handlers with dependency injection.
  container.RegisterSingleton(handlers);
  
  // Register the queue for WorkItemData. 
  container.RegisterSingleton<IQueue<WorkItemData>>(() => new InMemoryQueue<WorkItemData>());
  
  // The job runner will automatically look for and run all registered WorkItemHandlers.
  await JobRunner.RunContinuousAsync<WorkItemJob>(instanceCount: 2);
  ```
  
  ```
  Job.exe -t "Foundatio.Jobs.WorkItemJob, Foundatio"
  ```

  ```csharp
   // To trigger the job we need to queue the HelloWorldWorkItem message. 
   // This assumes that we injected an instance of IQueue<WorkItemData> queue
   
   // NOTE: You may have noticed that HelloWorldWorkItem doesn't derive from WorkItemData.
   // Foundatio has an extension method that takes the model you post and serializes it to the 
   // WorkItemData.Data property.
   await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
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
await storage.SaveFileAsync("test.txt", "test");
string content = await storage.GetFileContentsAsync("test.txt")
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
3. [MetricsNETClient](https://github.com/exceptionless/Foundatio/blob/master/src/MetricsNET/MetricsNETClient.cs): An [Metrics.NET](https://github.com/etishor/Metrics.NET) implementation.

We recommend using all of the `IMetricsClient` implementations as singletons. 

#### Sample

```csharp
await metrics.CounterAsync("c1");
await metrics.GaugeAsync("g1", 2.534);
await metrics.TimerAsync("t1", 50788);
```

### [Logging](https://github.com/exceptionless/Foundatio/tree/master/src/Core/Logging)

We provide a [fluent logging api](https://github.com/exceptionless/Foundatio/blob/master/src/Core/Logging/Logger.cs) that can be used to log messages throughout your application. This is really great because it allows you to log to different sources like NLog and change it at a later date without updating your whole application to use the latest and greatest logging framework on the market.

By default the logger will not write to anything, but you can configure what to write to by calling `Logger.RegisterWriter(Action<LogData> writer)`. 

#### Sample

```csharp
Logger.Info().Message("Application starting up").Write();
Logger.Error().Exception(ex).Message("Writing a captured exception out to the log.").Write();
```

## Sample Application
We both [slides](https://docs.google.com/presentation/d/1ax4YmfCdao75aEakjdMvapHs4QxvTZOimd3cHTZ9JG0/edit?usp=sharing) and a [sample application](https://github.com/exceptionless/Foundatio.Samples) that shows off how to use Foundatio.

## Roadmap

This is a list of high level things that we are planning to do:
- Async Support **(In Progress: Some of our implementations are already fully Async)** 
- dnx/vnext support
- [Let us know what you'd like us to work on!](https://github.com/exceptionless/Foundatio/issues)
