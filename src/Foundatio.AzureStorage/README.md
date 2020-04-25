![Foundatio](https://raw.githubusercontent.com/FoundatioFx/Foundatio/master/media/foundatio.png "Foundatio")

[![Build status](https://github.com/FoundatioFx/Foundatio.AzureStorage/workflows/Build/badge.svg)](https://github.com/FoundatioFx/Foundatio.AzureStorage/actions)
[![NuGet Version](http://img.shields.io/nuget/v/Foundatio.AzureStorage.svg?style=flat)](https://www.nuget.org/packages/Foundatio.AzureStorage/)
[![Slack Status](https://slack.exceptionless.com/badge.svg)](https://slack.exceptionless.com)

Pluggable foundation blocks for building loosely coupled distributed apps.
- [Caching](#caching)
- [Queues](#queues)
- [Locks](#locks)
- [Messaging](#messaging)
- [Jobs](#jobs)
- [File Storage](#file-storage)
- [Metrics](#metrics)

Includes implementations in Redis, Azure, AWS, RabbitMQ and in memory (for development). 

## Why Foundatio?
When building several big cloud applications we found a lack of great solutions (that's not to say there isn't solutions out there) for many key pieces to building scalable distributed applications while keeping the development experience simple. Here are a few examples of why we built and use Foundatio:
 * Wanted to build against abstract interfaces so that we could easily change implementations.
 * Wanted the blocks to be dependency injection friendly.
 * Caching: We were initially using an open source Redis cache client but then it turned into a commercial product with high licensing costs. Not only that, but there weren't any in memory implementations so every developer was required to set up and configure Redis.
 * Message Bus: We initially looked at [NServiceBus](http://particular.net/nservicebus) (great product) but it had high licensing costs (they have to eat too) but was not OSS friendly. We also looked into [MassTransit](http://masstransit-project.com/) but found Azure support lacking and local set up a pain. We wanted a simple message bus that just worked locally or in the cloud.
 * Storage: We couldn't find any existing project that was decoupled and supported in memory, file storage or Azure Blob Storage.

To summarize, if you want pain free development and testing while allowing your app to scale, use Foundatio!

## Implementations
- [Redis](https://github.com/FoundatioFx/Foundatio.Redis) - Caching, Storage, Queues, Messaging, Locks, Metrics
- [Azure Storage](https://github.com/FoundatioFx/Foundatio.AzureStorage) - Storage, Queues
- [Azure ServiceBus](https://github.com/FoundatioFx/Foundatio.AzureServiceBus) - Queues, Messaging
- [AWS](https://github.com/FoundatioFx/Foundatio.AWS) - Storage, Queues, Metrics
- [RabbitMQ](https://github.com/FoundatioFx/Foundatio.RabbitMQ) - Messaging
- [Minio](https://github.com/FoundatioFx/Foundatio.Minio) - Storage
- [Aliyun](https://github.com/FoundatioFx/Foundatio.Aliyun) - Storage
- [SshNet](https://github.com/FoundatioFx/Foundatio.Storage.SshNet) - Storage

## Getting Started (Development)

[Foundatio can be installed](https://www.nuget.org/packages?q=Foundatio) via the [NuGet package manager](https://docs.nuget.org/consume/Package-Manager-Dialog). If you need help, please [open an issue](https://github.com/FoundatioFx/Foundatio/issues/new) or join our [Slack](https://slack.exceptionless.com) chat room. We’re always here to help if you have any questions!

**This section is for development purposes only! If you are trying to use the Foundatio libraries, please get them from NuGet.**

1. You will need to have [Visual Studio Code](https://code.visualstudio.com) installed.
2. Open the `Foundatio.sln` Visual Studio solution file.

## Using Foundatio
The sections below contain a small subset of what's possible with Foundatio. We recommend taking a peek at the source code for more information. Please let us know if you have any questions or need assistance!

### [Caching](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Caching)

Caching allows you to store and access data lightning fast, saving you exspensive operations to create or get data. We provide four different cache implementations that derive from the [`ICacheClient` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/ICacheClient.cs):

1. [InMemoryCacheClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/InMemoryCacheClient.cs): An in memory cache client implementation. This cache implementation is only valid for the lifetime of the process. It's worth noting that the in memory cache client has the ability to cache the last X items via the `MaxItems` property. We use this in [Exceptionless](https://github.com/exceptionless/Exceptionless) to only [keep the last 250 resolved geoip results](https://github.com/exceptionless/Exceptionless/blob/master/src/Exceptionless.Core/Geo/MaxMindGeoIpService.cs).
2. [HybridCacheClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/HybridCacheClient.cs): This cache implementation uses the `InMemoryCacheClient` and uses the `IMessageBus` to keep the cache in sync across processes.
3. [RedisCacheClient](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Cache/RedisCacheClient.cs): A Redis cache client implementation.
4. [RedisHybridCacheClient](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Cache/RedisHybridCacheClient.cs): This cache implementation uses both the `RedisCacheClient` and `InMemoryCacheClient` implementations and uses the `RedisMessageBus` to keep the in memory cache in sync across processes. This can lead to **huge wins in performance** as you are saving a serialization operation and call to Redis if the item exists in the local cache.
5. [ScopedCacheClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Caching/ScopedCacheClient.cs): This cache implementation takes an instance of `ICacheClient` and a string `scope`. The scope is prefixed onto every cache key. This makes it really easy to scope all cache keys and remove them with ease.

#### Sample

```csharp
using Foundatio.Caching;

ICacheClient cache = new InMemoryCacheClient();
await cache.SetAsync("test", 1);
var value = await cache.GetAsync<int>("test");
```

### [Queues](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Queues)

Queues offer First In, First Out (FIFO) message delivery. We provide four different queue implementations that derive from the [`IQueue` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Queues/IQueue.cs):

1. [InMemoryQueue](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Queues/InMemoryQueue.cs): An in memory queue implementation. This queue implementation is only valid for the lifetime of the process.
2. [RedisQueue](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Queues/RedisQueue.cs): An Redis queue implementation.
3. [AzureServiceBusQueue](https://github.com/FoundatioFx/Foundatio.AzureServiceBus/blob/master/src/Foundatio.AzureServiceBus/Queues/AzureServiceBusQueue.cs): An Azure Service Bus Queue implementation.
4. [AzureStorageQueue](https://github.com/FoundatioFx/Foundatio.AzureStorage/blob/master/src/Foundatio.AzureStorage/Queues/AzureStorageQueue.cs): An Azure Storage Queue implementation.
4. [SQSQueue](https://github.com/FoundatioFx/Foundatio.AWS/blob/master/src/Foundatio.AWS/Queues/SQSQueue.cs): An AWS SQS implementation.

#### Sample

```csharp
using Foundatio.Queues;

IQueue<SimpleWorkItem> queue = new InMemoryQueue<SimpleWorkItem>();

await queue.EnqueueAsync(new SimpleWorkItem {
    Data = "Hello"
});

var workItem = await queue.DequeueAsync();
```

### [Locks](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Lock)

Locks ensure a resource is only accessed by one consumer at any given time. We provide two different locking implementations that derive from the [`ILockProvider` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Lock/ILockProvider.cs):

1. [CacheLockProvider](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Lock/CacheLockProvider.cs): A lock implementation that uses cache to communicate between processes.
2. [ThrottlingLockProvider](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Lock/ThrottlingLockProvider.cs): A lock implementation that only allows a certain amount of locks through. You could use this to throttle api calls to some external service and it will throttle them across all processes asking for that lock.
3. [ScopedLockProvider](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Lock/ScopedLockProvider.cs): This lock implementation takes an instance of `ILockProvider` and a string `scope`. The scope is prefixed onto every lock key. This makes it really easy to scope all locks and release them with ease.

It's worth noting that all lock providers take a `ICacheClient`. This allows you to ensure your code locks properly across machines.

#### Sample

```csharp
using Foundatio.Lock;

ILockProvider locker = new CacheLockProvider(new InMemoryCacheClient(), new InMemoryMessageBus());
var lock = await locker.AcquireAsync("test");
// ...
await lock.ReleaseAsync();

ILockProvider throttledLocker = new ThrottlingLockProvider(new InMemoryCacheClient(), 1, TimeSpan.FromMinutes(1));
var throttledLock = await throttledLocker.AcquireAsync("test");
// ...
await throttledLock.ReleaseAsync();
```

### [Messaging](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Messaging)

Allows you to publish and subscribe to messages flowing through your application. We provide four different message bus implementations that derive from the [`IMessageBus` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Messaging/IMessageBus.cs):

1. [InMemoryMessageBus](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Messaging/InMemoryMessageBus.cs): An in memory message bus implementation. This message bus implementation is only valid for the lifetime of the process.
2. [RedisMessageBus](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Messaging/RedisMessageBus.cs): A Redis message bus implementation.
3. [RabbitMQMessageBus](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/master/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBus.cs): A RabbitMQ implementation.
4. [AzureServiceBusMessageBus](https://github.com/FoundatioFx/Foundatio.AzureServiceBus/blob/master/src/Foundatio.AzureServiceBus/Messaging/AzureServiceBusMessageBus.cs): An Azure Service Bus implementation.

#### Sample

```csharp
using Foundatio.Messaging;

IMessageBus messageBus = new InMemoryMessageBus();
await messageBus.SubscribeAsync<SimpleMessageA>(msg => {
  // Got message
});

await messageBus.PublishAsync(new SimpleMessageA { Data = "Hello" });
```

### [Jobs](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Jobs)

Allows you to run a long running process (in process or out of process) without worrying about it being terminated prematurely. We provide three different ways of defining a job, based on your use case:

1. **Jobs**: All jobs must derive from the [`IJob` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Jobs/IJob.cs). We also have a [`JobBase` base class](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Jobs/JobBase.cs) you can derive from which provides a JobContext and logging. You can then run jobs by calling `RunAsync()` on the job or by creating a instance of the [`JobRunner` class](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Jobs/JobRunner.cs) and calling one of the Run methods. The JobRunner can be used to easily run your jobs as Azure Web Jobs.

  #### Sample

  ```csharp
  using Foundatio.Jobs;

  public class HelloWorldJob : JobBase {
    public int RunCount { get; set; }

    protected override Task<JobResult> RunInternalAsync(JobContext context) {
       RunCount++;
       return Task.FromResult(JobResult.Success);
    }
  }
  ```

  ```csharp
  var job = new HelloWorldJob();
  await job.RunAsync(); // job.RunCount = 1;
  await job.RunContinuousAsync(iterationLimit: 2); // job.RunCount = 3;
  await job.RunContinuousAsync(cancellationToken: new CancellationTokenSource(10).Token); // job.RunCount > 10;
  ```

2. **Queue Processor Jobs**: A queue processor job works great for working with jobs that will be driven from queued data. Queue Processor jobs must derive from [`QueueJobBase<T>` class](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Jobs/QueueJobBase.cs). You can then run jobs by calling `RunAsync()` on the job or passing it to the [`JobRunner` class](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Jobs/JobRunner.cs). The JobRunner can be used to easily run your jobs as Azure Web Jobs.

  #### Sample

  ```csharp
  using Foundatio.Jobs;

  public class HelloWorldQueueJob : QueueJobBase<HelloWorldQueueItem> {
    public int RunCount { get; set; }

    public HelloWorldQueueJob(IQueue<HelloWorldQueueItem> queue) : base(queue) {}

    protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<HelloWorldQueueItem> context) {
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
  container.AddSingleton<IQueue<HelloWorldQueueItem>>(s => new InMemoryQueue<HelloWorldQueueItem>());

  // To trigger the job we need to queue the HelloWorldWorkItem message.
  // This assumes that we injected an instance of IQueue<HelloWorldWorkItem> queue

  IJob job = new HelloWorldQueueJob();
  await job.RunAsync(); // job.RunCount = 0; The RunCount wasn't incremented because we didn't enqueue any data.

  await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
  await job.RunAsync(); // job.RunCount = 1;

  await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
  await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
  await job.RunUntilEmptyAsync(); // job.RunCount = 3;
  ```

3. **Work Item Jobs**: A work item job will run in a job pool among other work item jobs. This type of job works great for things that don't happen often but should be in a job (Example: Deleting an entity that has many children.). It will be triggered when you publish a message on the `message bus`. The job must derive from the [`WorkItemHandlerBase` class](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Jobs/WorkItemJob/WorkItemHandlerBase.cs). You can then run all shared jobs via [`JobRunner` class](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Jobs/JobRunner.cs). The JobRunner can be used to easily run your jobs as Azure Web Jobs.

  #### Sample

  ```csharp
  using System.Threading.Tasks;
  using Foundatio.Jobs;

  public class HelloWorldWorkItemHandler : WorkItemHandlerBase {
    public override async Task HandleItemAsync(WorkItemContext ctx) {
      var workItem = ctx.GetData<HelloWorldWorkItem>();

      // We can report the progress over the message bus easily.
      // To receive these messages just inject IMessageSubscriber
      // and Subscribe to messages of type WorkItemStatus
      await ctx.ReportProgressAsync(0, "Starting Hello World Job");
      await Task.Delay(TimeSpan.FromSeconds(2.5));
      await ctx.ReportProgressAsync(50, "Reading value");
      await Task.Delay(TimeSpan.FromSeconds(.5));
      await ctx.ReportProgressAsync(70, "Reading value.");
      await Task.Delay(TimeSpan.FromSeconds(.5));
      await ctx.ReportProgressAsync(90, "Reading value..");
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
  container.AddSingleton(handlers);

  // Register the queue for WorkItemData.
  container.AddSingleton<IQueue<WorkItemData>>(s => new InMemoryQueue<WorkItemData>());

  // The job runner will automatically look for and run all registered WorkItemHandlers.
  new JobRunner(container.GetRequiredService<WorkItemJob>(), instanceCount: 2).RunInBackground();
  ```

  ```csharp
   // To trigger the job we need to queue the HelloWorldWorkItem message.
   // This assumes that we injected an instance of IQueue<WorkItemData> queue

   // NOTE: You may have noticed that HelloWorldWorkItem doesn't derive from WorkItemData.
   // Foundatio has an extension method that takes the model you post and serializes it to the
   // WorkItemData.Data property.
   await queue.EnqueueAsync(new HelloWorldWorkItem { Message = "Hello World" });
  ```

### [File Storage](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Storage)

We provide different file storage implementations that derive from the [`IFileStorage` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Storage/IFileStorage.cs):

1. [InMemoryFileStorage](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Storage/InMemoryFileStorage.cs): An in memory file implementation. This file storage implementation is only valid for the lifetime of the process.
2. [FolderFileStorage](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Storage/FolderFileStorage.cs): An file storage implementation that uses the hard drive for storage.
3. [AzureFileStorage](https://github.com/FoundatioFx/Foundatio.AzureStorage/blob/master/src/Foundatio.AzureStorage/Storage/AzureFileStorage.cs): An Azure Blob storage implementation.
4. [S3FileStorage](https://github.com/FoundatioFx/Foundatio.AWS/blob/master/src/Foundatio.AWS/Storage/S3FileStorage.cs): An AWS S3 file storage implementation.
5. [RedisFileStorage](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Storage/RedisFileStorage.cs): An Redis file storage implementation.
6. [MinioFileStorage](https://github.com/FoundatioFx/Foundatio.Minio/blob/master/src/Foundatio.Minio/Storage/MinioFileStorage.cs) An Minio file storage implementation.
7. [AliyunFileStorage](https://github.com/FoundatioFx/Foundatio.Aliyun/blob/master/src/Foundatio.Aliyun/Storage/AliyunFileStorage.cs): An Aliyun file storage implementation.
8. [SshNetFileStorage](https://github.com/FoundatioFx/Foundatio.Storage.SshNet/blob/master/src/Foundatio.Storage.SshNet/Storage/SshNetFileStorage.cs): An SFTP file storage implementation.

We recommend using all of the `IFileStorage` implementations as singletons.

#### Sample

```csharp
using Foundatio.Storage;

IFileStorage storage = new InMemoryFileStorage();
await storage.SaveFileAsync("test.txt", "test");
string content = await storage.GetFileContentsAsync("test.txt")
```

### [Metrics](https://github.com/FoundatioFx/Foundatio/tree/master/src/Foundatio/Metrics)

We provide five implementations that derive from the [`IMetricsClient` interface](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Metrics/IMetricsClient.cs):

1. [InMemoryMetricsClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Metrics/InMemoryMetricsClient.cs): An in memory metrics implementation.
2. [RedisMetricsClient](https://github.com/FoundatioFx/Foundatio.Redis/blob/master/src/Foundatio.Redis/Metrics/RedisMetricsClient.cs): An Redis metrics implementation.
3. [StatsDMetricsClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio/Metrics/StatsDMetricsClient.cs): An statsd metrics implementation.
4. [MetricsNETClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio.MetricsNET/MetricsNETClient.cs): An [Metrics.NET](https://github.com/Recognos/Metrics.NET) implementation.
4. [AppMetricsClient](https://github.com/FoundatioFx/Foundatio/blob/master/src/Foundatio.AppMetrics/AppMetricsClient.cs): An [AppMetrics](https://github.com/AppMetrics/AppMetrics) implementation.
5. [CloudWatchMetricsClient](https://github.com/FoundatioFx/Foundatio.AWS/blob/master/src/Foundatio.AWS/Metrics/CloudWatchMetricsClient.cs): An [AWS CloudWatch](https://aws.amazon.com/cloudwatch/) implementation.

We recommend using all of the `IMetricsClient` implementations as singletons.

#### Sample

```csharp
IMetricsClient metrics = new InMemoryMetricsClient();
metrics.Counter("c1");
metrics.Gauge("g1", 2.534);
metrics.Timer("t1", 50788);
```

## Sample Application
We have both [slides](https://docs.google.com/presentation/d/1ax4YmfCdao75aEakjdMvapHs4QxvTZOimd3cHTZ9JG0/edit?usp=sharing) and a [sample application](https://github.com/FoundatioFx/Foundatio.Samples) that shows off how to use Foundatio.

## Roadmap

This is a list of high level things that we are planning to do:
- [Let us know what you'd like us to work on!](https://github.com/FoundatioFx/Foundatio/issues)
