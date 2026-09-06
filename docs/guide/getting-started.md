# Getting started

Foundatio supplies swappable caching, file storage, locking, messaging, and background job building blocks. Start with in-memory implementations, then choose a production provider for the contracts your application needs.

The messaging and durable job APIs shown here are the current unreleased redesign. Existing published provider packages may still use the earlier queue/pub-sub APIs; see the [migration guide](messaging.md#migration).

## Run the example

From a checkout of this revision:

```powershell
dotnet run --project samples/Foundatio.QuickstartSample
```

The sample starts a host, sends a command, publishes an event, runs a typed job with progress, and schedules a CRON cleanup. It requires no external services.

## A message worker

Reference `Foundatio` and `Foundatio.Extensions.Hosting` from this revision. `AddFoundatioWorker` is in the `Foundatio` namespace. The full message and handler definitions are in the quickstart sample above.

```csharp
using Foundatio;
using Foundatio.Messaging;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFoundatioWorker(foundatio => foundatio
    .Messaging.UseInMemory()
    .Messaging.AddConsumer<SendReceipt, SendReceiptHandler>()
    .Messaging.AddSubscriber<OrderPlaced, OrderPlacedHandler>("billing"));

await builder.Build().RunAsync();
```

Handlers implement `IMessageHandler<T>`. Send work with `IMessageBus.SendAsync`; publish events with `PublishAsync`. Queue consumers compete. A named event subscription receives one copy for its group, and replicas in that group compete. Handlers must tolerate duplicate delivery.

For a producer-only API, use `AddFoundatio().Messaging.UseInMemory()` instead. `AddFoundatio()` registers clients; `AddFoundatioWorker(...)` also starts background processing when the host starts. See [Messaging](messaging.md) for complete handler examples and delivery guarantees.

## Choose the operation

| You need to… | Use | Register on the worker |
| --- | --- | --- |
| Hand work to one available consumer | `bus.SendAsync(message)` | `AddConsumer<T, THandler>()` |
| Notify each interested service | `bus.PublishAsync(message)` | `AddSubscriber<T, THandler>("service-name")` |
| Track execution, progress, cancellation, or schedules | `jobs.EnqueueAsync<TJob, TArgs>(args)` | `AddJobType<TJob>("job-name.v1")` |

## Add the infrastructure you need

```csharp
builder.Services.AddFoundatio()
    .Caching.UseInMemory()
    .Storage.UseFolder("data")
    .Locking.UseCache();
```

Use `ICacheClient` for cache operations, `IFileStorage` for files, and `ILockProvider` for distributed coordination. Dispose streams and acquired locks. In-memory data is process-local and does not survive restarts.

Add [durable jobs](jobs.md) only when you need handles, progress, cancellation, stored retries, or schedules. `AddFoundatioWorker(...)` hosts the required worker, scheduler, and delayed-message dispatcher roles. [Individual hosting methods](dependency-injection.md#choose-host-roles-explicitly) support running those roles in separate processes.

For Redis, the builder shares and owns one connection by default. If you supply your own, keep it alive until the host has stopped; see [connection lifetime](dependency-injection.md#redis-connection-lifetime).

## Next steps

- [Worker queues](queues.md) for competing consumers and migration from `IQueue<T>`.
- [Messaging](messaging.md) for pub/sub identity, serialization, topology, retries, and provider behavior.
- [Durable jobs](jobs.md) for typed work, CRON definitions, monitoring, and retention.
- [Caching](caching.md), [storage](storage.md), and [locks](locks.md) for other infrastructure contracts.
