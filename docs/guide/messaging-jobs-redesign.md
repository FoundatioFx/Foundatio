# Messaging and Jobs Redesign

The redesigned messaging API is one client — `IMessageBus` in `Foundatio.Messaging` — with two verbs. The caller's verb carries the delivery semantic, and handlers are registered without any topology decision:

- `SendAsync` — a **command** / unit of work: exactly one handler instance across the fleet processes it (competing consumers).
- `PublishAsync` — an **event**: every subscribing service receives one copy (a scaled service's instances compete for it), or every instance when the subscription opts into `PerInstance`.

```csharp
await bus.SendAsync(new ResizeImage(id));        // one handler instance, somewhere, does the work
await bus.PublishAsync(new OrderSubmitted(id));  // every subscribing service hears about it
```

`SendBatchAsync` and `PublishBatchAsync` batch both verbs; the non-generic `IEnumerable<object>` overloads accept heterogeneous batches and group by resolved route. Per-operation options are `MessageSendOptions` and `MessagePublishOptions` (priority, delay/`DeliverAt`, TTL, correlation id, headers, and a `Destination`/`Topic` override as the escape hatch).

The legacy implementations are gone. What remains for migration is a thin, opt-in bridge: the old `IMessageBus`/`IMessagePublisher`/`IMessageSubscriber` interface definitions plus `LegacyMessageBusAdapter`, registered with `Messaging.AddLegacyAdapter()`, which maps old-style publish/subscribe calls onto the new bus (see the Migration section).

## The core owns behavior; transports stay simple

The division of responsibility is deliberate: **the core owns behavior, transports stay thin.** A transport is bytes in, bytes out plus a few primitives — `IMessageTransport` is `SendAsync`, `CompleteAsync`, `AbandonAsync`, and small opt-in operation interfaces (`ISupportsPull`, `ISupportsPush`, `ISupportsDeadLetter`, `ISupportsRedeliveryDelay`, `ISupportsVisibilityTimeout`, `ISupportsLockRenewal`, `ISupportsStats`, `ISupportsProvisioning`). Everything that defines *how messaging behaves* — serialization, routing, multi-type dispatch, settlement, scheduling, and especially **retry and dead-lettering** — lives in the core and is therefore identical across every transport. There is exactly one retry authority (the core), never a tug-of-war between core policy and a broker-native redrive policy.

Every transport API takes the same canonical identity: `DestinationAddress` (`Name`, `Role` — `Queue`/`Topic`/`Subscription`/`Binding` — and, for subscriptions, the owning `Topic`; created via `ForQueue`/`ForTopic`/`ForSubscription`). `Key` is its opaque string form (`"{topic}/{name}"` for subscriptions), so the same logical destination can never be spelled two ways on the send path versus the provisioning path.

Facts a transport advertises are **role-aware**: the core asks `ITransportInfo.GetCapabilities(DestinationRole)` and gets a `TransportCapabilities` record (`DelayedDelivery`, `MaxDeliveryDelay`, `Priority`, `Expiration`, `Ordering`, `MaxBatchSize`, `MaxMessageBytes`). Capabilities genuinely differ by role on real brokers — the AWS transport's queue role has a native 15-minute `MaxDeliveryDelay` (SQS `DelaySeconds`) while its topic role has no native delay at all. Anything not advertised is treated as unsupported: the core validates, falls back to the runtime store, or fails loudly — a broker never silently drops a requested behavior.

## Setup

```csharp
services.AddFoundatio()
    .Messaging
        .ConfigureRouting(r => r
            .UseServiceIdentity("billing")
            .MapQueue<OrderSubmitted>("orders")
            .MapTopic("order-events", typeof(IOrderEvent)))
        .ConfigureRetry(p => p with { MaxAttempts = 5 })
        .ConfigureTopology(TopologyMode.Ensure)
        .AddMessageType<OrderSubmitted>("order.submitted")
        .UseInMemory()
    .Messaging.AddHandler<OrderSubmitted, SendConfirmationHandler>()
    .Jobs.UseInMemory()
    .Jobs.AddJobType<RebuildSearchIndexJob>("search.rebuild");
```

Swap providers by swapping one line: `.Messaging.UseRedis()` (Redis Streams), `.Messaging.UseAws()` (SQS/SNS), `.Jobs.UseRedis()`, or `.Messaging.UseTransport(...)` / `.Jobs.UseRuntimeStore(...)` for anything custom. Application code depends on `IMessageBus`, `IJobClient`, and `IJobMonitor`; deployment or admin code can depend on `IMessageTopology`.

`AddMessageType<T>(name)` gives a type a stable wire discriminator so payloads survive assembly/namespace moves; unregistered types fall back to `Type.FullName` (never `AssemblyQualifiedName`). `.Jobs.AddJobType<TJob>(name)` does the same for persisted job types.

## Handlers

Handlers are topology-free. A handler implements `IMessageHandler<T>` and is registered declaratively; it never decides queue-vs-topic — the sender's verb does:

```csharp
public class SendConfirmationHandler : IMessageHandler<OrderSubmitted>
{
    public Task HandleAsync(IMessageContext<OrderSubmitted> context, CancellationToken cancellationToken)
        => _email.SendConfirmationAsync(context.Message.OrderId, cancellationToken);
}

services.AddFoundatio()
    .Messaging.AddHandler<OrderSubmitted, SendConfirmationHandler>(o =>
    {
        o.MaxConcurrency = 4;
        o.DeadLetterOn<ValidationException>();
    });
```

Each message is dispatched to the handler in its own DI scope (scoped dependencies work), and a single auto-registered hosted service (`MessageHandlerHostedService`) starts every registered handler at app start and disposes them at shutdown. Throwing from `HandleAsync` triggers the core retry/dead-letter policy. Each handler class defaults to its own subscriber group (the `SubscriptionQualifier` is set to the handler type name), so every handler registered for an event type receives its own copy of each published message.

For dynamic subscriptions, `IMessageBus.SubscribeAsync<T>(handler, options)` returns an `IMessageSubscription` handle (`Key`, `Destination`, `Topic`, `Subscription`, `Source`); disposing it detaches the handler.

### Subscription options

A subscription listens on the type's two delivery channels — sent commands and published events — and `MessageSubscriptionOptions` declares its intent:

- **`Deliveries`** — `MessageDeliveries.Sent`, `Published`, or `Both` (default). A handler that only ever consumes commands (or only events) states that so no idle listener is wired — and so a queue-only or topic-only transport can serve it. The default `Both` quietly narrows to what the transport supports; explicitly requesting a single channel the transport cannot serve throws `NotSupportedException`.
- **`Subscription`** — the subscriber-group identity for published messages. Defaults to the service identity, so all instances of a service share one subscription and compete. **`SubscriptionQualifier`** distinguishes groups within one service (`"{service-identity}.{qualifier}"`). **`PerInstance`** gives every running instance its own unique subscription (cache invalidation, config reload) and is mutually exclusive with `Subscription`.
- **`MaxConcurrency`** — messages processed concurrently per instance. Default 1: the only default that preserves per-handler ordering, and each handler already gets its own concurrent stream (10 handlers = 10 parallel consumers). Raise it for I/O-bound, order-agnostic handlers.
- **`MaxAttempts`**, **`RedeliveryBackoff`**, **`DeadLetterWhen`** — per-subscription retry overrides; null inherits the default `RetryPolicy`. **`DeadLetterOn<TException>()`** is the by-type shorthand for `DeadLetterWhen` and composes (call once per exception type).
- **`AckMode`** — `Auto` (default) or `Manual`.
- **`RouteType`**, **`Destination`**, **`Topic`** — grouped/interface consumption and per-subscription route overrides.
- **`Key`** — consumer identity. Subscriptions sharing a `Key` on the same channel form one competing group and must configure identical failure policies; the backoff/`DeadLetterWhen` **delegates are compared by identity**, so share the same delegate instances — a lambda recreated per subscription is rejected as a conflicting registration.

Delivery semantics are never invisible: each subscription logs its effective topology (destination, subscriber group, concurrency, retry posture) once at subscribe time.

## Routing and topology

`IMessageRouter` resolves the queue destination and topic for a message type. The default router's precedence:

```text
operation override > exact type map > interface/base-type map > MessageRouteAttribute > configured default > convention > kebab-cased type name
```

Configure routes once with `ConfigureRouting` (a `MessageRoutingOptionsBuilder`): `UseDefaultQueue`, `UseDefaultTopic`, `MapQueue<T>` / `MapQueue(destination, params Type[])`, `MapTopic<T>` / `MapTopic(topic, params Type[])`, `UseServiceIdentity`, `UseSubscriptionIdentity`, and `UseConvention`. `UseServiceIdentity` names the service (the default subscriber-group identity); when unset it falls back to the `FOUNDATIO_SUBSCRIPTION_ID` / `FOUNDATIO_SERVICE_ID` environment variables, then the kebab-cased app name.

**Routing configuration is also the topology declaration source.** `UseDefaultQueue`, `UseDefaultTopic`, `MapQueue`, and `MapTopic` declare the destinations they name, and setting a service/subscription identity declares the subscription on each configured topic — as `DestinationDeclaration` values carrying the *same* canonical `DestinationAddress` the runtime later sends to and receives from, so provisioning and runtime can never disagree on identity. Per-operation overrides are deliberately excluded: they are exceptional one-off routes.

```csharp
IMessageTopology topology = provider.GetRequiredService<IMessageTopology>();
IReadOnlyList<DestinationDeclaration> declarations = topology.GetDeclarations();
await topology.EnsureAsync();   // deploy/admin process with create permissions
await topology.ValidateAsync(); // check-only; throws naming what is missing
```

`TopologyMode` (via `ConfigureTopology`) governs how the client administers topology at runtime and at startup:

- **`Ensure`** (default) — create missing destinations on first use, and the handler host ensures the declared topology before any handler starts consuming.
- **`Validate`** — never create; verify each destination exists and throw when missing. Startup fails at boot instead of surfacing as runtime send errors.
- **`None`** — no topology calls at all; everything is pre-provisioned out of band.

The mode governs the core's provisioning calls; combine `Validate`/`None` with transport knobs such as `AwsMessageTransportOptions.AutoCreateDestinations = false` for a fully locked-down broker.

## Delivery settlement

Received messages surface as `IMessageContext` / `IMessageContext<T>` (id, body, headers, correlation id, priority, `Attempts`) and settle with two verbs:

```csharp
await context.CompleteAsync();                                                   // handled successfully
await context.RejectAsync();                                                     // retry (redelivery)
await context.RejectAsync(new RejectOptions { RedeliveryDelay = TimeSpan.FromSeconds(30) });
await context.RejectAsync(new RejectOptions { Terminal = true, Reason = "validation", Exception = ex });
await context.RenewLockAsync();                                                  // long handler heartbeat
```

A non-terminal reject returns the message for redelivery (optionally after `RedeliveryDelay`); `Terminal = true` means "never redeliver" and routes the message to the dead-letter sink with the `Reason` and `Exception` forensics attached. `RejectOptions.BestEffortDelay` lets a delay the transport cannot honor degrade to immediate redelivery instead of failing (the core's own retry policy uses best-effort delays; an explicit caller delay defaults to strict).

Auto-ack is the default: a handler that returns without settling is completed, and a handler that throws is rejected per the retry policy. Manual settlement is opt-in with `AckMode.Manual`.

## Retry and dead-lettering

The core owns retry and dead-lettering, so behavior is identical on every transport. A transport only redelivers abandoned messages and optionally exposes a dead-letter sink; the core decides how many times to retry, how long to wait, and when to give up — using the broker's own delivery count as the crash-safe attempt counter, so the core owns the *policy* without owning durable retry *state*.

The default `RetryPolicy`: `MaxAttempts` 5, and `RetryPolicy.DefaultBackoff` — an immediate first retry, then 10s/20s/30s (capped) with ±20% jitter, the delay shape mature messaging stacks converged on. Configure the default and override per subscription:

```csharp
services.AddFoundatio()
    .Messaging.ConfigureRetry(p => p with
    {
        MaxAttempts = 5,
        Backoff = attempt => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))),
        DeadLetterWhen = ex => ex is ValidationException,   // unrecoverable: dead-letter immediately
        DeadLetterDestination = "orders-dead-letter"        // null derives "{source}.deadletter"
    });
```

Deserialization failures are always treated as unrecoverable. Where a dead-lettered message lands, in order of preference: the transport's native dead-letter sink (`ISupportsDeadLetter`), otherwise the configured or derived (`"{source}.deadletter"`) dead-letter destination written by the core. Dead-lettered messages carry forensics headers — `message.dead_letter.reason`, `.attempts`, `.exception_type`, `.exception_message`, `.exception_stack`, `.failed_at`, and `.original_destination` (`KnownHeaders.DeadLetter*`) — so a dead message is triageable with plain transport tooling, and `ISupportsDeadLetter.ReceiveDeadLetteredAsync` reads raw entries back (including poison payloads that never deserialized).

We deliberately do **not** configure broker-native redrive policies (SQS `maxReceiveCount`, Azure Service Bus `MaxDeliveryCount`, RabbitMQ DLX): that would split authority between broker and core and make behavior transport-specific. A destination's structural creation knobs, if any, are limited to `DestinationDeclaration.ProviderArguments`.

### Delays and the runtime-store fallback

A send delay or redelivery backoff is served natively when the transport supports it within its advertised ceiling (`TransportCapabilities.MaxDeliveryDelay`, `ISupportsRedeliveryDelay.MaxRedeliveryDelay`). A delay the broker cannot honor — beyond SQS's 15-minute delivery delay, or any delayed publish on SNS — is parked in the durable runtime store instead of being silently truncated: `MessageBusOptions.RuntimeStore` takes an `IScheduledDispatchStore` (any `IJobRuntimeStore` satisfies it; the DI builder wires it automatically when a runtime store is configured), and the job runtime pump dispatches parked messages when due. If neither native support nor a store is available, the operation fails loudly rather than dropping the delay.

### Unmatched message types

A message arriving on a shared destination whose type has no registered consumer on this node — a newer message type mid rolling-deploy, or a misconfiguration — is surfaced loudly: it increments the `foundatio.messaging.unhandled` metric and throws `UnhandledMessageTypeException`, isolated to that one message so the receive loop and the other type handlers keep running. It is retried so a node that *does* handle the type can pick it up, and finally dead-lettered as `"no-handler"` after `RetryPolicy.UnmatchedMaxAttempts` (default 50) — a genuinely orphaned type cannot loop forever.

## Jobs

`IJobClient` submits durable work and returns a `JobHandle`; `IJobWorker` claims and executes; `IJobMonitor` queries state; `IJobRuntimeStore` persists all of it. Jobs implement `IJob`:

```csharp
public class RebuildSearchIndexJob : IJob
{
    public async Task<JobResult> RunAsync(JobExecutionContext context)
    {
        var args = context.GetArguments<RebuildSearchIndexArgs>();
        await context.ReportProgressAsync(50, "halfway");
        return JobResult.Success;
    }
}

JobHandle handle = await jobs.EnqueueAsync<RebuildSearchIndexJob, RebuildSearchIndexArgs>(new RebuildSearchIndexArgs { Index = "orders" });
JobState? state = await handle.GetStateAsync();
await handle.RequestCancellationAsync();
```

**Typed payloads.** `EnqueueAsync<TJob, TArgs>(args)` serializes the arguments into the durable `JobState.Payload` (with `PayloadType` stored as a discriminator for forensics); the job reads them via `JobExecutionContext.GetArguments<TArgs>()`, guarded by `HasArguments`. Mismatches throw a descriptive exception naming the stored type.

**Execution context.** `JobExecutionContext` carries `JobId`, `Attempt`, and the `CancellationToken`, plus the store-backed helpers useful inside job code: `ReportProgressAsync`, `RenewLeaseAsync` (heartbeat for long runs), and `IsCancellationRequestedAsync` (cooperative cancellation). Its public constructor creates a *detached* context for tests — helpers no-op, and an `arguments` object surfaces through `GetArguments` without serialization.

**The worker.** Every run gets its own async DI scope (scoped services resolve per run, not as accidental singletons). `JobWorker` runs a bounded pool — at most `maxConcurrency` jobs in flight, a slot freeing the moment a job settles — and claims are compare-and-set guarded so concurrency cannot double-run. Lease renewal is a supervised loop, not a fire-and-forget timer: a run is cancelled when its lease is lost to another node *or* when renewal keeps failing past the lease window (the lease has lapsed on the broker's clock too, so continuing would risk double-executing side effects); the terminal state transition is ownership-guarded so a stale worker cannot overwrite the new owner's state. Stale `Processing` jobs (a worker crash mid-run) are reclaimed and re-queued while attempts remain, then dead-lettered.

### CRON scheduling

```csharp
services.AddFoundatio()
    .Jobs.UseInMemory()
    .Jobs.AddCronJob<NightlyExportJob>("0 2 * * *", o =>
    {
        o.MaxRetries = 3;
        o.Arguments = new ExportArgs { Format = "csv" };
    });
```

`AddCronJob<TJob>(cron, o => ...)` registers a `ScheduledJobDefinition`; `CronJobOptions` covers `Name`, `Scope` (`Global` = one instance per tick, `PerNode` = every instance), `Overlap` (`SkipIfRunning` default), `MisfireWindow`, `MaxAttempts`, `TimeZone`, `Enabled`, and typed `Arguments` serialized into every occurrence's payload. Definitions are scheduled automatically when the pump starts — no manual `IScheduledJobStore.ScheduleAsync` call. The scheduler materializes every occurrence due within the misfire window (not just the latest) as durable, deduplicated store entries, and owns occurrence recovery with its own per-definition retry/dead-letter budget.

### Managing schedules at runtime

`IScheduledJobManager` (registered with the runtime) manages schedules while the app runs — both declaratively-registered CRON jobs and ones added on the fly share the same scheduler store:

```csharp
var cron = provider.GetRequiredService<IScheduledJobManager>();

await cron.ScheduleAsync(new ScheduledJobDefinition {         // add, or replace by name
    Name = "tenant-report", Cron = "0 6 * * *", JobType = typeof(TenantReportJob),
    Arguments = new ReportArgs { TenantId = tenantId } });

await cron.RescheduleAsync("tenant-report", "0 7 * * *");     // change just the schedule
await cron.SetEnabledAsync("tenant-report", false);           // pause (no occurrences materialize)
await cron.SetEnabledAsync("tenant-report", true);            // resume

JobHandle run = await cron.TriggerAsync("tenant-report");     // run NOW, independent of the cron
var state = await run.GetStateAsync();                        // watch it like any durable job

// Type-addressed overloads resolve the schedule name from the job type — the same
// default AddCronJob<TJob> uses when no explicit name is given:
var schedule = await cron.GetScheduleAsync<NightlyExportJob>();
await cron.SetEnabledAsync<NightlyExportJob>(false);
JobHandle manual = await cron.TriggerAsync<NightlyExportJob>();
```

`TriggerAsync` materializes a durable manual occurrence (unique `"{name}:manual:…"` id, never deduplicated) that the pump claims and executes with the definition's `Arguments` and retry/dead-letter budget, returning a `JobHandle` for progress watching and cancellation. Manual runs bypass `Overlap` accounting — the trigger is a deliberate operator action — and a disabled schedule refuses to trigger (enable it first). `GetSchedulesAsync`/`GetScheduleAsync`/`UnscheduleAsync` round out the surface.

### The runtime pump

`JobRuntimePumpService` is registered automatically with any runtime store, so a configured store can never silently accumulate work that nothing drains. Each poll it materializes CRON occurrences, then runs an **overlapped execution pass** — dispatching due work (message dispatches before job occurrences, so the messaging delayed-delivery fallback is never head-of-line blocked by a long job), recovering stale jobs, and running queued jobs. Scheduling keeps its cadence even while a long pass runs. Tune with `ConfigureRuntimePump`: `JobRuntimePumpOptions.Enabled` (false = manual control), `PollInterval` (1s), `BatchSize` (100), `MaxJobAttempts` (3), and `WorkerConcurrency` (1; every in-flight job still gets its own DI scope, lease, and cancellation watcher).

## Testing

`Foundatio.Testing` runs the real bus over a recording in-memory transport for deterministic, sleep-free tests:

```csharp
services.AddFoundatio()
    .Messaging.UseTestHarness()
    .Messaging.AddHandler<OrderPlaced, SendConfirmationHandler>();

// start hosted services, then:
await bus.PublishAsync(new OrderPlaced(42));
await harness.WaitForIdleAsync();
Assert.Single(harness.Published<OrderPlaced>());
Assert.Empty(harness.DeadLetteredMessages);
```

Resolve `MessagingTestHarness` from the container. `WaitForIdleAsync` blocks until every destination has nothing queued and nothing in flight (throws a `TimeoutException` naming the still-busy destinations). Recordings cover every movement — `SentMessages`, `PublishedMessages`, `HandledMessages`, `AbandonedMessages`, `DeadLetteredMessages`, with typed accessors `Sent<T>()` / `Published<T>()` / `Handled<T>()` / `Abandoned<T>()` / `DeadLettered<T>()` — so the core retry/dead-letter path is directly assertable: a message redelivered N times and then dead-lettered shows up as N abandonments plus one dead-letter.

## Migrating from the previous APIs

The old implementations (`InMemoryMessageBus`, `QueueBase`/`InMemoryQueue`, `JobBase`/`QueueJobBase`/`JobWithLockBase`/`JobRunner`, `WorkItemJob`, and the hosted `AddJob`/`AddDistributedCronJob` infrastructure) were removed. The mappings:

| Old | New |
|---|---|
| `IQueue<T>.EnqueueAsync(item)` | `IMessageBus.SendAsync(item)` — competing consumers, ack/retry/dead-letter are core-owned |
| `IQueue<T>.DequeueAsync` + worker loop | `AddHandler<T, THandler>()` — the hosted handler consumes; no polling code |
| `QueueJobBase<T>.ProcessQueueEntryAsync` | `IMessageHandler<T>.HandleAsync(IMessageContext<T>, ct)` |
| `IMessageBus.PublishAsync(msg, delay)` | `IMessageBus.PublishAsync(msg, new MessagePublishOptions { Delay = ... })` — delays are durable via the runtime store |
| `IMessageSubscriber.SubscribeAsync<T>(Func<T, ct, Task>)` | `SubscribeAsync<T>((ctx, ct) => ... ctx.Message ...)`, or keep the old code compiling with `Messaging.AddLegacyAdapter()` |
| `JobBase.RunAsync(CancellationToken)` / old `IJob` | `IJob.RunAsync(JobExecutionContext)` — use `context.CancellationToken`; `JobResult` is unchanged |
| `JobWithLockBase` | The durable runtime's lease already guarantees single ownership; `AddCronJob` scope `Global` covers scheduled exclusivity |
| `WorkItemJob` + `WorkItemHandlers` | `EnqueueAsync<TJob, TArgs>(args)` with `context.GetArguments<TArgs>()` and `context.ReportProgressAsync(...)` |
| `AddDistributedCronJob<TJob>(cron)` | `.Jobs.AddCronJob<TJob>(cron, o => ...)` — durable occurrences with retry/dead-letter, manageable via `IScheduledJobManager` |

**The messaging bridge**: `Messaging.AddLegacyAdapter()` registers the retained `Foundatio.Messaging.Legacy` interfaces (`IMessageBus`/`IMessagePublisher`/`IMessageSubscriber`) as a thin adapter over the new bus, so old consuming code compiles and interoperates with migrated code on the same transport. Old-style subscriptions map to per-instance, published-only subscriptions (the old fan-out semantics); `MessageOptions.UniqueId` is ignored (no broker dedup exists), and the old raw-envelope `IMessage` tap has no adapter path (the new bus is destination-scoped). Delete the `AddLegacyAdapter()` call when the last old-style call site is gone.

## Providers

- **In-memory** (`InMemoryMessageTransport`, `InMemoryJobRuntimeStore`) — the reference implementation for local dev and tests; supports every operation interface.
- **Redis** (`Foundatio.Redis`) — `RedisStreamsMessageTransport` (FIFO streams; delays route through the runtime store) and `RedisJobRuntimeStore`, wired via `.Messaging.UseRedis()` / `.Jobs.UseRedis()` over one shared connection.
- **AWS** (`Foundatio.Aws`) — `AwsMessageTransport` (queues on SQS, pub/sub on SNS+SQS) via `.Messaging.UseAws()`; role-aware capabilities as above, `AutoCreateDestinations` to control implicit resource creation, and LocalStack support via `ServiceUrl`.

A new provider is validated against the shared conformance suites in `Foundatio.TestHarness`: `MessageTransportConformanceTests` (send/receive, settlement, redelivery, dead-letter, visibility, provisioning — tests skip per unimplemented operation interface) and `JobRuntimeStoreConformanceTests` (state round-trips, CAS transitions, leases, stale recovery including the renew-during-reclaim race, and scheduled-dispatch claiming, driven by a fake time provider).
