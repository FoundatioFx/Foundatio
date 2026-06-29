# Messaging and Jobs Redesign

The new messaging API is app-facing and type-driven. Queue, pub/sub, received-message, headers, options, and common message abstractions live under `Foundatio.Messaging`; folders may separate queue, pub/sub, and provider contracts, but application code should start with `IQueue` and `IPubSub`.

Provider-facing transport contracts such as `IMessageTransport`, `ISupportsPull`, `ISupportsPush`, and `ISupportsDeadLetter` are still public so external providers can implement them. They are infrastructure contracts, not the primary application surface.

## The core owns behavior; transports stay simple

The division of responsibility is deliberate: **the core owns behavior, transports stay thin.** A transport is bytes in, bytes out plus a few primitives — send, receive, complete, abandon, and (optionally) a dead-letter sink. Everything that defines *how messaging behaves* — serialization, content types, routing, multi-type dispatch, priority, back-pressure, tracing, metrics, and especially **retry and dead-lettering** — lives in the core and is therefore identical across every transport. A provider only advertises which primitives it supports through small capability interfaces (`ISupportsPull`, `ISupportsPush`, `ISupportsDeadLetter`, `ISupportsDelayedDelivery`, `ISupportsRedeliveryDelay`, …); it never owns policy.

This keeps providers small and hard to get subtly wrong, and keeps behavior portable: code verified against the in-memory transport behaves the same on a real broker. It also avoids a split-brain retry model — there is exactly one authority (the core), never a tug-of-war between the core's `MaxAttempts` and a broker-native redrive policy. See [Retry and dead-lettering](#retry-and-dead-lettering).

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

### Multiple message types on one destination

Several message types can share a single destination. Start one typed consumer per type; they attach to a single underlying receive loop that dispatches each message to the consumer registered for its type (read from the `message.type` header):

```csharp
await using var submitted = await queue.StartConsumerAsync<OrderSubmitted>(HandleSubmittedAsync);
await using var cancelled = await queue.StartConsumerAsync<OrderCancelled>(HandleCancelledAsync);
// One loop on the shared destination. OrderSubmitted is dispatched to the first handler, OrderCancelled to the second.
```

Consumers that share a message type compete: each message is dispatched to one of them, round-robin. The non-generic `StartConsumerAsync` (or a consumer whose route type is an interface/base type) is a catch-all that receives any type no exact-typed consumer claimed — the grouped/raw-envelope path. All consumers on one destination must agree on `MaxConcurrency` (it is a property of the shared loop). A message whose type has **no** registered consumer on this node is handled loudly — see [Unmatched message types](#unmatched-message-types).

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

Received messages settle with two verbs — the same for queue and pub/sub:

```csharp
await message.CompleteAsync();                                              // handled successfully
await message.RejectAsync();                                               // retry, transport-timed redelivery
await message.RejectAsync(new RejectOptions { RedeliveryDelay = TimeSpan.FromSeconds(30) }); // retry after a delay
await message.RejectAsync(new RejectOptions { Terminal = true, Reason = "validation" });     // do not retry
await message.RenewLockAsync();
```

`RejectAsync` replaces the separate abandon and dead-letter verbs. A non-terminal reject returns the message for redelivery (optionally after `RedeliveryDelay`); `Terminal = true` means "never redeliver" and routes the message to the dead-letter sink, falling back to a configured destination or a drop (see below).

Auto-ack is the default: a handler that returns without settling is completed automatically, and a handler that throws is rejected according to the [retry policy](#retry-and-dead-lettering). Manual ack is opt-in with `AckMode.Manual` on the consumer options.

Unsupported capabilities fail clearly with `NotSupportedException` or a validation exception — there are no silent no-ops for lock renewal, priority, expiration, or delayed delivery. Terminal reject is the one deliberate exception: a transport with no dead-letter sink does not throw, it drops the message (at-most-once for terminal messages), which is the honest behavior for an ack-less/broadcast transport.

## Retry and dead-lettering

The core owns retry and dead-lettering, so behavior is identical on every transport (see [The core owns behavior](#the-core-owns-behavior-transports-stay-simple)). A transport only has to redeliver an abandoned message and, optionally, expose a dead-letter sink; the core decides how many times to retry, how long to wait between attempts, and when to give up. The broker's own delivery count is used as a crash-safe attempt counter, so the core owns the *policy* without owning durable retry *state*.

Configure a default policy and override it per consumer:

```csharp
services.AddFoundatio()
    .Messaging.ConfigureRetry(r => r with {
        MaxAttempts = 5,
        Backoff = attempt => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))),
        DeadLetterDestination = "orders-dead-letter"
    });

await queue.StartConsumerAsync<OrderSubmitted>(HandleAsync, new QueueConsumerOptions {
    MaxAttempts = 10 // per-consumer override; null inherits the default policy
});
```

When a handler throws, the message is retried (abandoned for redelivery, with the configured backoff) until `MaxAttempts` is reached, then dead-lettered. Where a dead-lettered message lands, in order of preference:

1. the transport's native dead-letter sink, when it has one (`ISupportsDeadLetter`) — preserving native DLQ tooling;
2. otherwise the configured `RetryPolicy.DeadLetterDestination`, which the core writes to directly (a normal queue on the same transport), recording the reason in the `message.dead_letter.reason` header;
3. otherwise the message is dropped (at-most-once) — the honest outcome when there is nowhere durable to park it.

We deliberately do **not** configure broker-native redrive policies (SQS `maxReceiveCount`, Azure Service Bus `MaxDeliveryCount`, RabbitMQ DLX). That would split authority between the broker and the core and make behavior transport-specific. The core is always authoritative; transports stay simple. A destination's structural creation knobs, if any, are limited to `DestinationDeclaration.ProviderArguments`.

### Delayed redelivery and capability bounds

An explicit `RedeliveryDelay` (or a configured `Backoff`) is served natively when the transport supports it within its advertised limit — `ISupportsRedeliveryDelay.MaxRedeliveryDelay` and `ISupportsDelayedDelivery.MaxDeliveryDelay`. A delay longer than the broker can honor — for example beyond SQS's 15-minute delivery delay or 12-hour visibility window — is routed through the durable job runtime store instead of being silently truncated. If neither native support nor a runtime store is available, the operation fails loudly rather than dropping the delay.

### Unmatched message types

A message that arrives on a destination but whose type has no registered consumer on this node — for example a newer message type during a rolling deploy, before every node has been updated — is surfaced loudly rather than quietly swallowed. It increments the `foundatio.messaging.unhandled` metric and throws `UnhandledMessageTypeException`, isolated to that one message so the receive loop and the other type handlers keep running. The message is retried so a node that *does* handle the type can pick it up, and is finally dead-lettered as `"no-handler"` once `RetryPolicy.UnmatchedMaxAttempts` (default 50) is exhausted — so a genuinely orphaned type cannot loop forever.

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
