# Messaging and Jobs Redesign

The new messaging API is app-facing and type-driven. Queue, pub/sub, received-message, headers, options, and transport abstractions live under `Foundatio.Messaging`; folders may separate queue, pub/sub, and transport code, but consumers should not need a separate queue namespace for the new API.

## Setup

Register the in-memory messaging transport and durable job runtime through DI:

```csharp
services.AddFoundatio()
    .Messaging.UseInMemory()
    .Jobs.UseInMemoryRuntime();
```

Application code should depend on `Foundatio.Messaging.IQueue`, `IPubSub`, `IJobClient`, `IJobMonitor`, and `IJobWorker` instead of constructing `InMemoryMessageTransport`, `MessageQueue`, `PubSub`, or `JobClient` directly.

## Queue

The default queue model is send or receive this message type:

```csharp
await queue.EnqueueAsync(new OrderSubmitted(id));

IReceivedMessage<OrderSubmitted>? received = await queue.ReceiveAsync<OrderSubmitted>();
```

Destination and source are advanced overrides:

```csharp
await queue.EnqueueAsync(message, new QueueMessageOptions {
    Destination = "orders-high-priority"
});

IReceivedMessage<OrderSubmitted>? received = await queue.ReceiveAsync<OrderSubmitted>(new QueueReceiveOptions {
    Source = "orders-high-priority"
});
```

Consumers return handles and do not block unexpectedly:

```csharp
await using IMessageConsumer consumer = await queue.StartConsumerAsync<OrderSubmitted>(HandleAsync);
```

Use `RunConsumerAsync` when the desired behavior is a blocking lifetime loop.

## Pub/Sub

Pub/sub follows the same type-driven pattern:

```csharp
await pubsub.PublishAsync(new OrderSubmitted(id));

await using IMessageSubscription subscription = await pubsub.SubscribeAsync<OrderSubmitted>(
    HandleAsync,
    new PubSubSubscriptionOptions { Subscription = "billing-service" });
```

`PubSubMessageOptions` mirrors queue send options where concepts overlap: priority, delay, TTL, correlation id, deduplication id, headers, and topic override.

## Routing

Default route precedence is:

```text
options override > resolver/registration > MessageRouteAttribute > kebab-case type-name convention
```

`QueueMessageOptions.Destination`, `QueueReceiveOptions.Source`, `PubSubMessageOptions.Topic`, and `PubSubSubscriptionOptions.Topic`/`Subscription` are explicit operation overrides. `QueueOptions.DestinationResolver`, `PubSubOptions.TopicResolver`, and `PubSubOptions.SubscriptionResolver` are the registration/resolver layer. `MessageRouteAttribute` is the type-local fallback before the final convention.

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
