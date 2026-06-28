# Messaging and Jobs Redesign

The new messaging API is app-facing and type-driven. Queue, pub/sub, received-message, headers, options, and common message abstractions live under `Foundatio.Messaging`; folders may separate queue, pub/sub, and provider contracts, but application code should start with `IQueue` and `IPubSub`.

Provider-facing transport contracts such as `IMessageTransport`, `ISupportsPull`, `ISupportsPush`, and `ISupportsDeadLetter` are still public so external providers can implement them. They are infrastructure contracts, not the primary application surface.

## Setup

Register the in-memory messaging transport, central routing policy, and durable job runtime through DI:

```csharp
services.AddFoundatio()
    .Messaging.ConfigureRouting(r => r
        .MapQueue<OrderSubmitted>("orders")
        .MapTopic("order-events", typeof(IOrderEvent))
        .UseSubscriptionIdentity("billing-service"))
    .UseInMemory()
    .Jobs.UseInMemoryRuntime()
    .Jobs.Register<RebuildSearchIndexJob>("search.rebuild");
```

Application code should depend on `Foundatio.Messaging.IQueue`, `IPubSub`, `IJobClient`, `IJobMonitor`, and `IJobWorker` instead of constructing `InMemoryMessageTransport`, `MessageQueue`, `PubSub`, or `JobClient` directly. Deployment or admin code can depend on `IMessageTopology` to inspect, create, or validate the destinations implied by routing configuration.

## Queue

The default queue model is send or receive this message type:

```csharp
await queue.EnqueueAsync(new OrderSubmitted(id));

IReceivedMessage<OrderSubmitted>? received = await queue.ReceiveAsync<OrderSubmitted>();
```

Destination and source are advanced operation overrides:

```csharp
await queue.EnqueueAsync(message, new QueueMessageOptions {
    Destination = "orders-high-priority"
});

IReceivedMessage<OrderSubmitted>? received = await queue.ReceiveAsync<OrderSubmitted>(new QueueReceiveOptions {
    Source = "orders-high-priority"
});
```

Grouped or default queues can be consumed through the raw envelope path:

```csharp
IReceivedMessage? received = await queue.ReceiveAsync(new QueueReceiveOptions {
    RouteType = typeof(IOrderMessage)
});

await queue.EnqueueBatchAsync(new object[] {
    new OrderSubmitted(id),
    new OrderCancelled(id)
});
```

Consumers return handles and do not block unexpectedly:

```csharp
await using IMessageConsumer consumer = await queue.StartConsumerAsync<OrderSubmitted>(HandleAsync);
```

Use `RunConsumerAsync` when the desired behavior is a blocking lifetime loop. Starting the same consumer key with the same handler and options is idempotent; starting the same key with conflicting handler/options throws.

## Pub/Sub

Pub/sub follows the same type-driven publishing pattern:

```csharp
await pubsub.PublishAsync(new OrderSubmitted(id));

await using IMessageSubscription subscription = await pubsub.SubscribeAsync<OrderSubmitted>(HandleAsync);
```

Topic routing and subscription identity are separate. The topic answers where the event is published. The subscription answers which logical service or consumer group receives it. Multiple instances using the same subscription compete on the same transport subscription; different subscriptions on the same topic receive fan-out copies:

```csharp
services.AddFoundatio()
    .Messaging.ConfigureRouting(r => r
        .MapTopic("order-events", typeof(IOrderEvent))
        .UseSubscriptionIdentity("billing-service"));
```

Advanced operation overrides remain available:

```csharp
await pubsub.PublishAsync(message, new PubSubMessageOptions {
    Topic = "order-events-replay"
});

await using IMessageSubscription subscription = await pubsub.SubscribeAsync<OrderSubmitted>(
    HandleAsync,
    new PubSubSubscriptionOptions {
        Topic = "order-events-replay",
        Subscription = "billing-replay"
    });
```

`PubSubMessageOptions` mirrors queue send options where concepts overlap: priority, delay, TTL, correlation id, deduplication id, headers, and topic override. `PubSubSubscriptionOptions.Key` is only the local duplicate-listener key; `Subscription` is the transport consumer group identity. `PublishBatchAsync(IEnumerable<object>)` supports heterogeneous event batches and groups sends by resolved topic.

## Routing

Default route precedence is:

```text
operation override > explicit route map > interface/base-type map > MessageRouteAttribute > configured default/convention
```

`IMessageRouter` is shared by queues and pub/sub. Configure routes once with `MessageRoutingOptionsBuilder`:

```csharp
services.AddFoundatio()
    .Messaging.ConfigureRouting(r => r
        .UseDefaultQueue("all-work")
        .UseDefaultTopic("all-events")
        .MapQueue<OrderSubmitted>("orders")
        .MapQueue("orders", typeof(OrderSubmitted), typeof(OrderCancelled))
        .MapQueue("order-work", typeof(IOrderMessage))
        .MapTopic("order-events", typeof(IOrderEvent))
        .UseConvention(ctx => $"app-{ctx.MessageType.Name.ToLowerInvariant()}"));
```

`QueueMessageOptions.Destination`, `QueueReceiveOptions.Source`, `PubSubMessageOptions.Topic`, and `PubSubSubscriptionOptions.Topic`/`Subscription` are final escape hatches for one operation. Attribute routing remains available for type-local defaults, but central routing should be the normal path.

Routing configuration is also the topology declaration source. `UseDefaultQueue`, `UseDefaultTopic`, `MapQueue`, and `MapTopic` declare the queue destinations or topics they name; `UseSubscriptionIdentity` declares subscriptions for configured topics. Operation-level overrides are intentionally not part of startup topology because they are exceptional one-off routes.

```csharp
IMessageTopology topology = provider.GetRequiredService<IMessageTopology>();
IReadOnlyList<DestinationDeclaration> declarations = topology.GetDeclarations();
await topology.EnsureAsync();   // deploy/admin process with create permissions
await topology.ValidateAsync(); // app startup check without creating destinations
```

## Delivery Settlement

Received messages use explicit settlement verbs for both queue and pub/sub:

```csharp
await message.CompleteAsync();
await message.AbandonAsync();
await message.DeadLetterAsync("validation");
await message.RenewLockAsync();
await message.ReportProgressAsync(50, "half");
```

Unsupported capabilities fail clearly with `NotSupportedException` or a validation exception. There are no silent no-ops for dead-lettering, lock renewal, progress, priority, expiration, or delayed delivery.

## Jobs

`IJobClient` submits durable work and returns a `JobHandle`; it does not execute jobs synchronously:

```csharp
JobHandle handle = await jobs.EnqueueAsync<RebuildSearchIndexJob>();
JobState? state = await handle.GetStateAsync();
```

Execution belongs to `IJobWorker`, which claims queued jobs from `IJobRuntimeStore`. State and operational queries belong to `IJobMonitor`. Scheduled occurrences are created by `IJobScheduler` and materialized by `JobScheduleProcessor` through the runtime store.

Persisted job type names come from `IJobTypeRegistry`. Register stable names for jobs that may move between assemblies or namespaces:

```csharp
services.AddFoundatio()
    .Jobs.Register<RebuildSearchIndexJob>("search.rebuild")
    .Jobs.UseInMemoryRuntime();
```

Unregistered jobs fall back to `Type.FullName`, not `AssemblyQualifiedName`.

## Migration

Legacy queue code usually moves from one queue instance per payload type to one app-facing queue plus routing:

```csharp
// Legacy
await queue.EnqueueAsync(new OrderSubmitted(id)); // IQueue<OrderSubmitted>

// New
await queue.EnqueueAsync(new OrderSubmitted(id)); // Foundatio.Messaging.IQueue
```

Legacy `IMessageBus` publish/subscribe code maps to `IPubSub` with explicit subscription identity:

```csharp
// Legacy
await messageBus.PublishAsync(new OrderSubmitted(id));
await messageBus.SubscribeAsync<OrderSubmitted>(HandleAsync);

// New
await pubsub.PublishAsync(new OrderSubmitted(id));
await using var subscription = await pubsub.SubscribeAsync<OrderSubmitted>(HandleAsync);
```

For per-type routing, register each type. For grouped routing, map an interface or base type. For default/global-style routing, set one default queue destination or topic for otherwise unmapped messages. Operation-level overrides should be reserved for exceptional paths such as replays or priority lanes.

### `IQueue` name collision during migration

Two public `IQueue` types coexist while the legacy queue is still shipped:

- `Foundatio.Queues.IQueue` / `IQueue<T>` — the legacy one-type-per-queue API.
- `Foundatio.Messaging.IQueue` — the new app-facing queue.

A file that has `using` directives for both namespaces will get a `CS0104` ambiguous-reference error on the bare name `IQueue`. Until the legacy API is removed, disambiguate per file with a `using` alias rather than fully qualifying every usage:

```csharp
using IQueue = Foundatio.Messaging.IQueue;       // new code
// or, while finishing a migration:
// using LegacyQueue = Foundatio.Queues.IQueue<MyMessage>;
```

New application code should depend on `Foundatio.Messaging.IQueue`; the alias keeps call sites clean without dropping the legacy namespace a file may still need mid-migration.

## Rollout Notes

The in-memory transport proves the API shape and conformance coverage for local development. Before locking this as a stable public API, validate at least one external provider against the same routing, topic/subscription, delayed delivery, dead-letter, TTL, priority, and batch constraints.
