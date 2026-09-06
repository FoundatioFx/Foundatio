# Dependency injection

Use `AddFoundatioWorker(configure)` for an application that processes messages or jobs. Put its Foundatio registrations in the callback; it starts the roles those registrations require. For producer-only APIs and manually driven tests, use `AddFoundatio()` instead.

```csharp
using Foundatio;

builder.Services.AddFoundatioWorker(foundatio => foundatio
    .Caching.UseInMemory()
    .Storage.UseInMemory()
    .Locking.UseCache()
    .Messaging.UseInMemory()
    .Messaging.AddConsumer<ProcessOrder, ProcessOrderHandler>()
    .Messaging.AddSubscriber<OrderPlaced, OrderPlacedHandler>("billing")
    .Jobs.UseInMemory()
    .Jobs.AddJobType<GenerateReportJob>("generate-report.v1"));
```

## Choose host roles explicitly

`AddFoundatio()` starts no hosted services. For split deployments, register clients, stores, handlers, and job types with it, then choose only the roles that process runs. These individual methods live in `Foundatio.Extensions.Hosting.Messaging` and `Foundatio.Extensions.Hosting.Jobs`.

| Registration | Responsibility |
| --- | --- |
| `AddMessageConsumers()` | Start registered consumers/subscribers and apply startup topology policy |
| `AddMessagingTopology()` | Optional producer-only topology startup checks |
| `AddJobWorker(concurrency)` | Execute registered job types |
| `AddJobScheduler()` | Reconcile declarations and materialize due CRON occurrences |
| `AddScheduledMessageDispatcher()` | Send messages parked in an `IScheduledDispatchStore` |

`AddFoundatioWorker` selects these roles for a combined worker: consumers when a transport is configured, worker and scheduler when job types are registered, and delayed dispatch when both a transport and dispatch store are configured. Set its `jobConcurrency` argument to control simultaneous job executions; set message concurrency on each receiving endpoint.

Scheduler, worker, and dispatcher loops run independently. A long-running job does not block delayed-message delivery or schedule materialization. Host registrations are idempotent.

## Service lifetimes and ownership

Keep infrastructure clients, transport connections, stores, and buses as singletons. Inject interfaces into business services. Handler and job dependencies may be scoped, including database contexts; a fresh scope is created for each invocation and disposed after execution.

The container owns transports registered through the builder. For manual construction, `MessageBus` owns its supplied transport by default. Set `MessageBusOptions.OwnsTransport = false` only when another owner manages that transport. Dispose directly created buses and use `await using` for subscriptions, received deliveries, and locks.

Avoid capturing a scoped dependency inside a singleton factory or long-lived delegate. Class handlers with constructor injection are the usual choice:

```csharp
public sealed class ProcessOrderHandler(OrderService orders) : IMessageHandler<ProcessOrder>
{
    public Task HandleAsync(IMessageContext<ProcessOrder> context, CancellationToken token)
        => orders.ProcessAsync(context.Message, token);
}
```

## Providers and testing

Swap `.Messaging.UseInMemory()` for a supported production transport, or `.Jobs.UseInMemory()` for a durable store. Check the [provider matrix](messaging.md#provider-guarantees): ordering, temporary subscriptions, native delays, and dead-letter administration are not identical across brokers.

Use `.Messaging.UseTestHarness()` and `.Jobs.UseTestHarness()` from `Foundatio.Testing` for tests that exercise the real runtime. Messaging tests explicitly start consumers. Job tests drive the harness worker/scheduler themselves; no background worker races their assertions. Dispose each test's service provider to isolate resources.

For keyed caches or storage services, standard `AddKeyedSingleton` and `[FromKeyedServices]` remain available. A single message bus routes by queue/topic; use explicit destinations and subscriber names instead of registering a separate typed queue service for every message type.

See [Getting started](getting-started.md), [Messaging](messaging.md), and [Durable jobs](jobs.md) for current setup and migration examples.
