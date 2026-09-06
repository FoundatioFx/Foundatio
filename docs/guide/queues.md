# Worker queues

Queued work uses the same `IMessageBus` client as pub/sub, with explicit consumer registration. `SendAsync` targets competing consumers; `PublishAsync` targets event subscriptions.

```csharp
builder.Services.AddFoundatioWorker(foundatio => foundatio
    .Messaging.UseInMemory()
    .Messaging.AddConsumer<ProcessOrder, ProcessOrderHandler>());

await bus.SendAsync(new ProcessOrder(1001));
```

Implement `IMessageHandler<ProcessOrder>` and process the message in `HandleAsync`. Successful handlers are acknowledged automatically; failures use the retry/dead-letter policy. Replicas compete for the queue, and each invocation gets its own dependency injection scope. Use idempotent processing because a message may be delivered again after a crash or uncertain acknowledgement.

For manual loops, `ReceiveAsync<T>` returns a disposable delivery with complete, reject, and renewal operations. Disposing an unfinished delivery returns it for redelivery. See [Messaging](messaging.md) for direct receive examples, concurrency, delays, topology, dead-letter administration, and provider guarantees.

Use [durable jobs](jobs.md) when a caller needs a job handle, progress, persisted execution retries, cancellation, or CRON scheduling.

## Migrating IQueue

| Former API | Current pattern |
| --- | --- |
| `IQueue<T>.EnqueueAsync` | `IMessageBus.SendAsync` |
| `QueueJobBase<T>` / queue worker callbacks | `IMessageHandler<T>` and `AddConsumer` |
| `DequeueAsync` | `ReceiveAsync<T>` and `await using` |
| Queue entry completion/abandonment | Delivery `CompleteAsync` / `RejectAsync` |
| `WorkItemJob` handlers | Typed `IJob<TArgs>` and `EnqueueAsync<TJob,TArgs>` |
| Queue-specific retry settings | Consumer retry options and the message bus retry policy |

Earlier external provider packages implement the former queue interfaces; they do not automatically implement the new transport contract. This unreleased revision supplies in-memory, Redis Streams, and SQS/SNS transports. Check the [provider matrix](messaging.md#provider-guarantees) before changing implementations.
