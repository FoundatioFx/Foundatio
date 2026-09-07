# Messaging

Use `IMessageBus` for two common patterns: `SendAsync` queues work for competing consumers; `PublishAsync` sends an event to each existing subscription. Queue consumers and event subscribers are registered separately. Publishing without subscriptions drops the event.

## Start with a worker

```csharp
using Foundatio;
using Foundatio.Messaging;

builder.Services.AddFoundatioWorker(foundatio => foundatio
    .UseServiceName("billing")
    .ConfigureMessaging(messaging => messaging.UseInMemory()
        .AddConsumer<SendReceipt, SendReceiptHandler>()
        .AddSubscriber<OrderPlaced, OrderPlacedHandler>()));
```

Handlers implement `IMessageHandler<T>`:

```csharp
public sealed class SendReceiptHandler(ReceiptService receipts) : IMessageHandler<SendReceipt>
{
    public Task HandleAsync(IMessageContext<SendReceipt> context, CancellationToken token)
        => receipts.SendAsync(context.Message.OrderId, token);
}
```

Each invocation gets its own dependency injection scope. A successful handler is acknowledged automatically; an exception follows the retry policy and eventually parks the message in a dead-letter destination. Use the supplied cancellation token for downstream work.

```csharp
await bus.SendAsync(new SendReceipt(1001));
await bus.PublishAsync(new OrderPlaced(1001));
```

Use `AddFoundatio().Messaging.UseInMemory()` in an API process that only produces messages. Client and storage registration never starts background execution. `AddMessagingTopology()` optionally ensures or validates declared producer destinations at host startup.

The [quickstart sample](https://github.com/FoundatioFx/Foundatio/tree/feat/messaging-jobs/samples/Foundatio.QuickstartSample) is a complete, executable host without external services. The [messaging sample](https://github.com/FoundatioFx/Foundatio/tree/feat/messaging-jobs/samples/Foundatio.MessagingSample) uses SQS/SNS and Redis jobs.

## Subscription identity

A named subscription such as `"billing"` is durable. All replicas using that name compete for the subscription's events. A separate `"analytics"` subscription gets its own copy. Names are deployment contracts: keep them stable across restarts and class renames. Register the subscription before publishing events that it must receive; creating one does not replay earlier publications.

`AddSubscriber<T, THandler>()` defaults to `UseServiceName(...)`, then `IHostEnvironment.ApplicationName`. This gives one copy per service, with replicas competing. Configure a stable service name for deployed contracts; outside a host, supply it or an explicit subscription name. `AddSubscriber<T, THandler>("name")` overrides the default and rejects blank names. Use `.Messaging.AddTemporarySubscriber<T, THandler>()` when each running instance needs its own temporary subscription.

For dynamic `SubscribeAsync`, an unnamed subscription is temporary and receives its own copy while its listener is alive. In-memory and Redis transports support renewable two-minute subscription leases. Disposal deletes the subscription, and loss of renewal expires it after a crash. Transient renewal errors retry within the lease; a lost lease stops the old receiver and recreates its subscription. `IMessageSubscription.Status`, `RecoveryVersion`, and `WaitUntilReadyAsync` expose recovery. Consumers maintaining derived state must resynchronize after a possible gap. Redis physically removes expired groups during subsequent stream operations. AWS requires a named durable subscription because SQS/SNS does not provide this expiration contract; unnamed subscriptions fail explicitly.

```csharp
await using var consumer = await bus.ConsumeAsync<SendReceipt>(
    (context, token) => receipts.SendAsync(context.Message.OrderId, token));

await using var subscriber = await bus.SubscribeAsync<OrderPlaced>(
    (context, token) => billing.RecordAsync(context.Message, token),
    new MessageSubscriptionOptions { Subscription = "billing" });
```

Concurrency belongs to a receiving endpoint and defaults to one. Set `MaxConcurrency` on consumer/subscription options when handlers may run concurrently. One bus rejects duplicate handlers for the same message type on the same endpoint; use separate named subscriptions when multiple event handlers each need a copy. Handlers sharing an endpoint must agree on concurrency; retry overrides belong to each registered handler. An endpoint permits at most one interface/raw fallback, avoiding ambiguous dispatch.

## Receive and settle directly

A hosted handler is optional. Direct receive returns a disposable delivery:

```csharp
await using var delivery = await bus.ReceiveAsync<SendReceipt>();
if (delivery is not null)
{
    await receipts.SendAsync(delivery.Message.OrderId, delivery.CancellationToken);
    await delivery.CompleteAsync();
}
```

Disposing an unsettled delivery returns it for redelivery. Raw receive takes `MessageReceiveOptions` with an explicit destination. Lease renewal runs while a delivery is active. Losing the lease cancels its work; disposal, shutdown, or cancellation does not count as successful completion.

Manual acknowledgement is available through `AckMode.Manual`. The endpoint retains its concurrency slot until settlement. Use automatic acknowledgement for ordinary handlers.

## Delivery, identity, and serialization

Durable delivery is **at least once**. A worker can finish a business operation and crash before acknowledgement. Lease renewal reduces concurrent execution but cannot guarantee exactly-once side effects. In-memory state is lost when the process stops. An interrupted broker receive can leave a message invisible until its lease expires (the bus requests a one-minute receive lease), including when a client cancels an SQS long poll before receiving its response. Allow for that redelivery delay during worker replacement.

The application `MessageId`, broker entry ID, and per-delivery receipt are distinct. Send/publish return the application ID. Supply `MessageSendOptions.MessageId` or `MessagePublishOptions.MessageId` for retry correlation and consumer deduplication; supplying an ID does not make broker sends idempotent. Scheduling, retries, and dead-lettering preserve that ID.

Batch sends are not transactions. On failure, `MessageSendException.Outcomes` describes each input as accepted, rejected, unknown, or not attempted, with its original index, application ID, provider error and retryability when known. An unknown outcome may already have reached the broker. Retrying requires an application deduplication strategy. Preserve individual IDs with the batch-item overload:

```csharp
await bus.SendBatchAsync<SendReceipt>([
    new MessageBatchItem<SendReceipt>(new SendReceipt(1001), "receipt-1001"),
    new MessageBatchItem<SendReceipt>(new SendReceipt(1002), "receipt-1002")
]);
```

AWS uses native batches of up to ten, respecting encoded payload/attribute limits and retaining mixed per-entry outcomes. Redis pipelines bounded batches (64 by default, configurable up to 256). Durable retry and dead-letter source records are removed only after verified acceptance.

Concurrent AWS sends, publishes and acknowledgements are automatically combined into native batches. Applications keep using the ordinary single-message methods, and completion still waits for the broker's per-entry response. Automatic batching uses separate send and acknowledgement buffers per destination: 100 buffered messages and four concurrent requests by default; additional callers await capacity. Partial batches collect for up to two milliseconds (subject to timer scheduling); an idle SQS sender can dispatch immediately, and a stream of singleton batches skips repeated collection delays while no request is active. `AwsMessageTransportOptions` exposes `EnableBatching`, `BatchDelay`, `MaxPendingBatchMessages`, `MaxConcurrentBatches` and `BatchTimeout` (30 seconds) for explicit tuning. Acknowledgement collection also learns the requested receive capacity, so a consumer with fewer than ten slots can flush a complete batch immediately; queued receipts can still fill all ten native slots. Disabling automatic batching leaves explicit batch sends available.

Canceling a caller does not cancel other messages sharing its AWS request. The collector skips canceled buffered operations; cancellation racing dispatch can leave an unknown send outcome. Disposal drains admitted operations and cancels unfinished requests at the batch timeout. Missing or failed delete results never count as acknowledgements. The AWS receiver caps each pull at ten and overlaps up to four receive requests when consumer capacity permits. Receives share one slot budget and collect freed slots together, starting immediately when a batch fills. `MaxConcurrency` remains a strict bound on unacknowledged deliveries; a slow handler does not block unrelated slots. Completed handlers release their slots while bounded cancellation cleanup finishes. Shutdown drains both handlers and cleanup.

AWS stores readable JSON/text payloads directly in the body and binary payloads as base64. A versioned `fnd.envelope` attribute carries the encoding, application ID, content type and headers. Headers are duplicated as native attributes only when selected in `AwsMessageTransportOptions.NativeMessageHeaders`, for SNS filters or external consumers. Select any required application headers (up to nine); the default empty list keeps the wire representation compact. For example, `NativeMessageHeaders = [KnownHeaders.MessageType, "tenant.id"]` exposes the type and tenant for SNS attribute filtering. Invalid or reserved attribute names fail during transport construction. The receiver accepts the earlier separate-attribute encoding, but earlier experimental receivers cannot read this new format. Upgrade producers and consumers together or use a new resource prefix. Existing experimental SNS attribute filters must explicitly select their header names; all consumer headers remain available without this option. These changes are confined to the unreleased provider.

For long-lived contracts, register versioned wire names on producers and consumers:

```csharp
builder.Services.AddFoundatio().Messaging
    .AddMessageType<OrderPlaced>("order-placed.v1", topic: "orders");
```

Bind the stable wire name and producer route together with `AddMessageType<T>(name, queue: ..., topic: ...)`, or set `MessageTypeName` plus `Destination`/`Topic` in a handler registration. Startup topology checks validate wire-name collisions even in `TopologyMode.None`; `MessageRoutingOptions.GetRouteMaps()` and startup logs expose declared mappings. Configure stable queue/topic routes independently of CLR class names. Concrete handlers may use the default CLR full-name discriminator; interface, abstract, and `object` receivers accept only explicitly registered concrete types. Sends and batches preserve the runtime payload type in the wire header, even when a variable is declared as an interface; the declared type still selects the route. The runtime does not scan assemblies or activate a type named by an untrusted header. Producers and consumers must agree on serialization and schema evolution. JSON uses `application/json`; other serializers default to byte-safe `application/octet-stream` unless configured otherwise.

When updating a business database and publishing must commit together, persist an outbox record in the same database transaction and publish from an outbox dispatcher. Foundatio does not coordinate that transaction. Consumers should commit their deduplication record with their business changes. Scheduled dispatch send/delete and retry park/ack are also at-least-once boundaries.

## Delays and failures

Native delays are used when the destination supports them. `Messaging.UseInMemory()` and `Messaging.UseRedis()` automatically supply a matching `IScheduledDispatchStore` without registering job execution. The automatic Redis store shares the transport connection, clock and key prefix; `RedisStreamsMessageTransportOptions.Scheduling` sets its limits. AWS requires an explicit durable store for delays beyond native support; configure `Messaging.UseSchedulingStore(...)` or share a job runtime store. `AddFoundatioWorker` starts its dispatcher when both a transport and dispatch store are registered; split deployments can call `AddScheduledMessageDispatcher()` directly. Messaging depends only on that store contract; a job worker is not required. `IJobRuntimeStore` also implements the dispatch store, so a configured job store can be shared.

```csharp
builder.Services.AddFoundatioWorker(foundatio => foundatio
    .ConfigureMessaging(messaging => messaging.UseInMemory()));
```

For production durability use a durable dispatch store, such as Redis. Without a suitable native delay or dispatch store, unsupported delays fail instead of being shortened. The scheduled message dispatcher runs independently of job execution.

Native dead-letter transports expose `ISupportsDeadLetter`: `PeekDeadLetteredAsync` reads a bounded page without removing evidence; `DeleteDeadLetteredAsync` removes an explicit ID; `ReplayDeadLetteredAsync` sends that ID to an explicit queue/topic and resets retry metadata while preserving the application ID. Peeking repeatedly is safe. Replaying can repeat business effects, so apply the same idempotency rules as normal delivery.

Unmatched message types retry after five seconds with jitter (50 attempts by default), allowing rolling deployments without a tight redelivery loop. Ordinary handler failures use five attempts with immediate-first, then 10/20/30-second jittered delays. Override these through `ConfigureRetry`. Malformed AWS envelope entries retain raw evidence and are quarantined independently, allowing valid entries in the batch to proceed.

If native dead-lettering is unavailable, the core sends to a fallback queue and only completes the original after that send succeeds. Failure to park the message leaves the original recoverable. AWS fallback queues support ordinary receive/settle operations, not non-destructive peek by ID.

## Topology

`TopologyMode.Ensure` creates destinations on first use. Successful permanent provisioning is cached briefly; errors invalidate it, and deleted receive destinations are recreated under listener supervision. Expiring declarations are never cached. `Validate` checks existing destinations and fails if missing; it does not create. `None` assumes out-of-band provisioning. This policy applies to sends, publishes, receiving, delayed dispatch, and fallback dead-letter sends. Temporary subscriptions require `Ensure`.

Producer routing declares queues/topics, never phantom subscriber groups. AWS resource existence and deletion work through a fresh transport instance, including SNS bindings. Provider administration should use `ISupportsProvisioning` explicitly.

## Provider guarantees

| Behavior | In-memory | Redis Streams | AWS SQS/SNS |
| --- | --- | --- | --- |
| Queued work and named event subscriptions | Yes, process-local | Yes | Yes |
| Temporary expiring subscriptions | Yes, supervised recovery | Yes, supervised recovery | Unsupported; name the subscription |
| Hybrid-cache invalidation | Resynchronizes after listener gaps | Resynchronizes after listener gaps | Fails immediately: temporary subscriptions required |
| Cache-backed lock notifications | Notifications plus polling | Notifications plus polling | Polling fallback |
| Delayed-message persistence | Automatic, process-local | Automatic, Redis | Explicit durable store for non-native delays |
| Execution durability after process loss | No | Depends on Redis persistence/HA | Broker-managed |
| Delivery order | Initial FIFO; priority/retries can reorder | Initial FIFO; retries/concurrency can reorder | Standard queues, no ordering guarantee |
| Native delayed queue send | No | No | Up to 15 minutes |
| Native delayed publish | No | No | No |
| Lease renewal | Yes | Atomic receipt fencing | SQS visibility; stale-receipt detection is best effort |
| Non-destructive DLQ peek/replay by ID | Yes | Yes, per subscription | No; core fallback queue |
| Backlog limit | Process memory | `MaxPendingMessages`, default 100,000 per stream/DLQ | Broker limits |

Redis capacity rejects new messages instead of trimming unread or pending entries. A slow durable subscription therefore applies backpressure to the topic. Acknowledged topic entries are trimmed only when every subscription has progressed past them. Retention checks are amortized to a one-second cadence and forced before capacity rejection. Empty Redis receivers back off from 25 ms to one second; tune `PollInterval` and `MaxIdlePollInterval` when idle latency matters. Delete abandoned durable subscriptions deliberately; temporary leases are not a replacement for durable subscription administration.

SQS/SNS support varies by destination role. Do not infer topic capabilities from queue capabilities. The shared conformance suite exercises in-memory, Redis, and SQS/SNS via LocalStack in CI; the emulator is not evidence of a live AWS deployment.

## Migration

The former publish-only interfaces live under `Foundatio.Messaging.Legacy`; `Messaging.AddLegacyAdapter()` adapts them to this bus. Legacy `IQueue<T>` workers migrate to an explicit consumer and `SendAsync`. There is no `Deliveries.Both`, handler-name-derived subscriber identity, or `PerInstance` flag. Choose `AddConsumer` or `AddSubscriber`, and use a stable service identity or explicit names for durable subscribers.

## Broker-driven execution tracking

For queues that need progress, cancellation, and operational history, configure `.Messaging.UseInMemoryExecutionTracking()` or `.Messaging.UseRedisExecutionTracking()`. This registers `IMessageExecutionStore`; it does not create runnable jobs, a job worker, or a second scheduler. Delivery remains owned by the message bus.

`MessageExecutionPipeline` runs a raw delivery callback with `MessageProcessingContext`, applies returned `MessageOutcome` values, and persists only confirmed settlement. Pass it to a manual-ack raw `ConsumeAsync` endpoint with `WaitForManualSettlement = false`. The producer creates a unique execution state before sending and puts its ID in `ExecutionHeaders.ExecutionId`. Integrations such as Foundatio.Mediator perform this composition automatically.

Processing claims and subsequent progress/status writes are fenced by the broker attempt number. A terminal execution is not started again. Expired history does not block valid broker work; progress/history updates then become no-ops. Tracking is not persistent deduplication or exactly-once delivery. Broker acceptance and persistence are separate operations; uncertain sends and acknowledgments remain nonterminal. Application side effects must tolerate retries.

Endpoint options own `MaxConcurrency`, `PrefetchCount`, `VisibilityTimeout`, `AutoRenewLock`, and `ShutdownTimeout`. Manual and automatic renewal share one conservative delivery deadline. Lost ownership cancels processing. Stopping receiving allows admitted handlers to drain within the configured shutdown timeout.

`ConsumeWithOutcomeAsync` accepts `MessageOutcome.Success`, `Retry`, `DeadLetter`, or `Unsettled` without requiring expected application failures to throw exceptions.

## Per-node broadcasts

`SubscribeNodeAsync` receives an independent best-effort copy on each running node, acknowledging before callbacks. Memory and Redis use native expiring subscriptions. AWS creates a tagged SQS queue and SNS subscription, heartbeats ownership, cleans up on disposal, and reaps stale resources when another node starts. AWS managed resources do not advertise native TTL expiration. Configure IAM for the required resource lifecycle, tagging, and discovery operations, including when durable topology is externally provisioned.

Use this for invalidation and UI refresh signals. A disconnected, paused, or restarting node may miss events and should refresh authoritative state. Use durable service subscriptions for work that needs replay and retries.

## Operational recovery

`MessageAdministration` supplies statistics, bounded dead-letter inspection, deletion, and replay through native capabilities. Providers without native inspection use bounded receive/hold/release. Scans stop at 1,000 messages or ten seconds; their cleanup has an independent timeout. A successful replacement send precedes deleting the original. That fallback is not transactional and an uncertain send can produce a duplicate. Callers may prepare fresh execution metadata for an operator replay while retaining the failed history.
