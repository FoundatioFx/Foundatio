# Building a Transport or Job Store

A provider implements a small contract and proves it by passing a conformance suite. Foundatio supplies everything else: serialization, routing, retries and backoff, dead-letter decisions, concurrency limits, lease renewal, delay emulation, graceful shutdown, tracing and metrics.

## Message transport

Implement `IMessageTransport` (4 methods) and `IMessageReceiver` (5 methods). [View source](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio/Messaging/Transport)

```csharp
public interface IMessageTransport : IAsyncDisposable
{
    TransportCapabilities Capabilities { get; }
    Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken ct = default);
    Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken ct = default);
    Task DeleteAsync(MessageSource source, CancellationToken ct = default);
    Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken ct = default);
}

public interface IMessageReceiver : IAsyncDisposable
{
    MessageSource Source { get; }
    Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken ct = default);
    Task CompleteAsync(IncomingMessage message, CancellationToken ct = default);
    Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken ct = default);
    Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken ct = default);
    Task RenewAsync(IncomingMessage message, CancellationToken ct = default);
}
```

What you must provide:

| Concept | Requirement |
|---|---|
| Queue | Competing receivers; sending before any receiver exists must not lose messages. |
| Topic + durable subscription | Each named subscription gets a copy and keeps it while no receiver is open. A new subscription starts with messages published after it is created. |
| One subscription | `MessageDestination.Subscription(topic, name)` delivers to that subscription only (used to replay its dead letters). Like a queue, it must not lose messages sent before a receiver exists. A send to a subscription that does not exist creates it, as opening a receiver does, so messages published to the topic afterwards reach it too; most brokers can't hold the message without creating the entity anyway. |
| Instance subscription | A copy for each open receiver; removed when the receiver is disposed or its process dies. |
| Dead-letter queue | `DeadLetterAsync` moves a message (id, body, headers kept, plus a reason header) to a queue receivable as `MessageSource.DeadLetter(source)`. A message at `MaxMessageSize` must fit with any reason: you may shorten the reason, keeping at least its first 1,024 UTF-8 bytes. Dead-lettering the same id again (after a replay) keeps both. |
| Deletion | `DeleteAsync` removes a queue or durable subscription with its messages and dead letters, or only the dead letters of a `DeadLetter` source. It succeeds when nothing exists and throws `ArgumentException` for instance subscriptions. Deduplication state may outlive it. You may refuse with `MessageBusException`, keeping the messages, while a receiver is open on the source in any process (Kafka does); otherwise a receiver still open on the deleted source, a durable subscription as much as a queue, must keep receiving new messages or throw from `ReceiveAsync` (preferably `MessageReceiverFaultedException`), never go silently deaf. |
| Leases | A received message is invisible for `LeaseDuration`, and `RenewAsync` makes it invisible for `LeaseDuration` from then; unsettled messages are delivered again with a higher `DeliveryCount`, not before the lease ends. If your broker decides the lease itself (a Service Bus lock duration is set per entity), set `IncomingMessage.LeaseExpiresAt` to when it ends, on this process's clock, in `ReceiveAsync` (bound it from when the receive returned, not when it started, since a long poll can outlast the lock before the message arrives) and again in `RenewAsync` when the broker extends it by its own duration. Foundatio then renews a third of the way through what is left of that lease, and bounds renewal retries by it, instead of following `LeaseDuration`. Only the transport writes it. The suite checks that it lies in the future and within the lease the broker granted, and that the message is not delivered again before it, so set `ShortLease` in your test class to the lease your broker grants there. |
| Lost leases | Once a lease is lost (it expired and the message went out again, its receiver or transport was disposed, or its partition moved), `RenewAsync`, `AbandonAsync` and `DeadLetterAsync` on it throw `MessageLeaseLostException` (or `ObjectDisposedException` after a dispose) and leave the newer delivery alone. `CompleteAsync` must too, unless the broker cannot tell a stale receipt from the current one (SQS): then it may remove the message instead, and your test class overrides `StaleCompleteMayRemoveTheMessage` to say so. Check the lease you know of before an abandon or dead-letter where the broker accepts a stale one silently (the Service Bus emulator abandons an expired lock without an error). A renew after the lease expired but before anyone else got the message may take the lease back or throw. |
| Bytes and headers | Body, headers and `GroupKey` delivered unchanged. Header names are case-sensitive: `X-Tenant` and `x-tenant` are two headers. Header names starting with `x-foundatio-` are reserved for your wire format: you may reject them with `ArgumentException` or pass them through. Ids, group keys and deduplication ids are 1 to `OutgoingMessage.MaxIdLength` (128) characters of any kind, spaces, non-ASCII and U+0000 included; map them if your broker or store is stricter (a PostgreSQL `text` column cannot hold U+0000). Names only contain letters, digits, `.`, `_` and `-`; shorten or hash them if your broker needs to, deterministically, from the source's own fields (not `ToString()`). A queue and a topic may have the same name: keep them apart if your broker's queues and topics share one namespace (Service Bus does). |
| Counts | `GetStatsAsync` reports a queue's or durable subscription's `Queued`, `Delayed`, `InFlight` and `DeadLettered` messages; see [Counts](#counts). |
| Size | Accept any message within `MaxMessageSize`, measured by `MessageSize.Of` (body, headers, message id, group key and deduplication id), binary bodies included, on queues and topics alike, and throw `MessageTooLargeException` above it. `MessageSize.Of` counts no per-header overhead: declare the limit after your own encoding overhead, reserving framing for at least 100 headers and the dead-letter reason. Split a batch that exceeds your broker's request size. |

All members may be called concurrently: settlement and renewal run while `ReceiveAsync` is waiting, and `ReceiveAsync` throws `OperationCanceledException` promptly when cancelled. `ReceiveAsync` returns as soon as a message is there, including one another process sent while it waited (wake waiting receives, don't only poll), and returns empty only once `maxWait` has passed. Disposing the transport closes its receivers, and their unsettled messages go to the next receiver, in this process or another, with a higher `DeliveryCount`: where closing a link releases messages without counting a delivery (Service Bus), abandon them first. An instance subscription left behind by a crashed process is your responsibility to remove; a heartbeat with an expiry, swept from the same timer that sends your heartbeats (or on publish), works well. A settlement call that fails for another reason must leave the message to be delivered again within a bounded time: after its lease, or, if leases don't expire, after a short delay you choose (Kafka waits five seconds), never only when the receiver closes. Throw broker and connection errors as `MessageBusException`, with the client's exception as its inner exception, so apps catch one type on every transport; let `OperationCanceledException` through. When `SendAsync` throws, forget the deduplication ids of the messages it did not send, or the caller's retry is dropped. `CreateReceiverAsync` returns once the receiver can deliver: wait for the broker's assignment, such as a Kafka consumer group join, so the first receive sees messages already waiting.

Declare honestly what your broker does natively in `TransportCapabilities`; Foundatio emulates or works around the rest. Each row says when to declare a capability. How core uses them (re-sent delays, retry holds, read-ahead depth, concurrent receives) is in the [design doc](https://github.com/FoundatioFx/Foundatio/blob/main/docs/design/messaging-jobs-v2.md#messaging-semantics); you don't need it to write a transport.

| Capability | Declare it when | Default |
|---|---|---|
| `MaxDelay`, `MaxPublishDelay`, `MaxRetryDelay` | Your broker, or your transport, delays sends, publishes and `AbandonAsync` retries: `null` for unlimited, the limit, or `TimeSpan.Zero` for none. The last two default to `MaxDelay`. Foundatio re-sends longer send delays, holds retries in-process when there is no retry delay, and rejects publishes beyond `MaxPublishDelay`. | `null` |
| `MinRetryDelay` | Your broker's delays are coarse (SQS visibility timeouts are whole seconds). Foundatio holds shorter retries in-process, leased, and lengthens one within 20% of it. | zero |
| `MaxSendBatchSize`, `MaxReceiveBatchSize`, `MaxMessageSize`, `MaxLeaseDuration` | Always: your broker's limits, `MaxMessageSize` after your encoding overhead (see Size above), `null` for none. | 100, 100, `null`, `null` |
| `MaxConcurrentReceives` | A receive is slow next to the handlers and returns few messages (SQS: at most 10 per call), so Foundatio should run several at once on one receiver. Concurrent calls must never hand out the same message, nor, with `OrderedGroups`, a message of a group while an earlier one returned by another call is unsettled; the suite checks both when you declare more than 1. | 1 |
| `ReceiveAhead` | A receive is one round trip that returns the messages the broker has at once (SQS, Redis, Service Bus without sessions). Leave it off when your receiver already buffers (Kafka, RabbitMQ), receives in the background (Service Bus sessions) or serves receives from memory. Messages Foundatio reads ahead are unsettled deliveries: count them wherever you count leased messages. | false |
| `LeaseExpiration` | An unsettled message is delivered again once its lease runs out. Leave it off for connection-held deliveries (RabbitMQ) or offsets (Kafka); Foundatio still calls `RenewAsync` about once a second while handling, so throw `MessageLeaseLostException` once a delivery has gone to another consumer (a revoked partition) and the handler is cancelled. | true |
| `OrderedGroups` | `GroupKey` maps to something your broker orders: SQS message groups, Service Bus sessions, Kafka partition keys. See the rules below. | false |
| `OneOutstandingPerGroup` | With `OrderedGroups`, your broker hands out at most one message of a group at a time, so no receive ever returns two messages of one group (Redis). With `ReceiveAhead`, Foundatio then reads ahead in full batches even with fewer handlers than a batch; without it, a batch is kept within the handler slots so every message of a group it may hold has one. A receive that breaks the promise has the messages beyond the slots abandoned, with an error logged. | false |
| `Deduplication` | Your broker drops a repeated `DeduplicationId` sent to the same destination within its window, in the same batch or from another process. The same id on another destination is not a duplicate. | false |

With `OrderedGroups`, deliver a group in send order: the order within one `SendAsync` call and of calls that complete one after another (concurrent senders may interleave), where a delayed message, or one abandoned with a delay, holds back its group's later ones. Never hand out a message of a group to any receiver, in any process, while an earlier one is unsettled. One receive may return several in-order messages of a group; Foundatio handles them in sequence and abandons the rest if one is retried. A group belongs to its source: the same key on another queue or subscription is another group. Dead-letter sources are not ordered. A table-backed transport must order by a value assigned in commit order, or serialize a group's inserts (PostgreSQL: `pg_advisory_xact_lock` per group), since a sequence is allocated before its row commits and two concurrent sends of a group can commit out of id order.

What the in-repo transports declare:

| Transport | Delays (send, publish, retry) | `MinRetryDelay` | `MaxConcurrentReceives` | `ReceiveAhead` | `LeaseExpiration` | `OrderedGroups` | `Deduplication` |
|---|---|---|---|---|---|---|---|
| In-memory | unlimited | — | 1 | no | yes | yes | yes |
| Redis | unlimited | — | 1 | yes | yes | yes | yes |
| SQS | 13 days, 13 days, 12 hours | 1 s | 16 | yes | yes | FIFO queues | FIFO queues |
| Service Bus | unlimited | 1 s | 4, 1 with sessions | without sessions | yes | with sessions | with duplicate detection |
| Storage Queues | 7 days | 1 s | 8 | yes | yes | no | no |
| Kafka | unlimited with `DelayedDelivery`, else none | 1 s | 1 | no | no | yes | no |
| RabbitMQ | about 8.5 years with `DelayedDelivery`, else none | 1 s | 1 | no | no | no | no |

The suite skips tests for capabilities you don't declare.

When a receiver can never deliver again (a fatal client error), throw `MessageReceiverFaultedException` from `ReceiveAsync`: Foundatio opens a new receiver and receives on it at once, while the failed one's in-flight messages finish (up to the shutdown timeout); then it disposes the failed one, so two receivers of a source can be open at once for a while. Other receive errors are retried with backoff (up to 10 seconds), and after five in a row the receiver is replaced too; failures of receives in flight together count once. Foundatio never disposes a receiver while a receive is in flight on it, and before it disposes one it cancels the handlers of the deliveries it still holds, so a replacement's redelivery never runs alongside them. Deliveries the broker releases on its own, such as those of a channel or connection it closed or of a partition the consumer lost, it may redeliver at once while their handlers still run; Foundatio cancels a handler when `RenewAsync` throws `MessageLeaseLostException`, which it calls about once a second, so throw it for such deliveries as soon as you know.

Foundatio settles each message as soon as its handler returns, often many at once, so `CompleteAsync` per message costs a broker call per message. If your broker settles several in one call (SQS `DeleteMessageBatch`, a Redis script, a SQL `DELETE ... WHERE id = ANY(...)`), coalesce them with `SettlementBatcher<TEntry>` from `Foundatio.Messaging.Transport`: one per receiver and kind of call, created with your batch size and a function that sends a batch and returns each entry's error (or null when all succeeded). An entry goes out at once while no call is in flight; otherwise it waits for the next call, which goes as soon as one returns or a batch is waiting, so a lone settlement never waits and a busy source settles in full batches. Foundatio lets up to a receive batch of settlements wait without their handler slots, even with one handler, so the batches can fill. The SQS and Redis transports use it for completes, and SQS for renewals and abandons too.

Push-based client libraries (RabbitMQ consumers, the Service Bus processor) can buffer deliveries in a bounded channel inside the receiver and return them from `ReceiveAsync`. `PrefetchCount` is how many to buffer ahead of what Foundatio is handling. Foundatio may hold far more deliveries unsettled than it handles at once (retries it holds, settlements in flight and read-ahead: up to 13 × `MaxConcurrency` plus the read-ahead depth), so a broker whose prefetch counts every unsettled delivery (RabbitMQ) must still deliver to a caller that holds more; the suite receives 100 messages without settling them. Derive from `IncomingMessage` to carry your receipt (delivery tag, receipt handle, stream id).

### Counts {#counts}

`GetStatsAsync(source)` feeds `IMessageAdmin.GetStatsAsync` and the backlog of the [monitor](./monitoring#backlog-counts), which may call it every few seconds for every source:

| Count | Messages |
|---|---|
| `Queued` | Waiting to be delivered now: not delayed and not leased to a receiver |
| `Delayed` | Waiting for a delivery time in the future, including retries waiting out their delay |
| `InFlight` | Delivered to a receiver and not yet completed, abandoned or dead-lettered |
| `DeadLettered` | In the source's dead-letter queue |

- Leave a count null when your broker cannot tell it; never estimate one you cannot read. Return null instead of a `MessageSourceStats` for a source you cannot count at all, such as an instance subscription.
- A source that does not exist yet counts as empty: return zeros (or nulls), don't throw, and don't create it.
- Throw `ArgumentException` for a `DeadLetter` source: its count is `DeadLettered` of the source it belongs to.
- The counts may be approximate and lag sends and settlements by a few seconds, as broker statistics do. Keep the call cheap (one round trip, or a few), and never wait for in-flight work to settle.
- Set `Source` to the source you were asked about.

### Prove it

```csharp
public class MyTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    protected override IMessageTransport CreateTransport() => new MyTransport(...);
}
```

Start there. The suite (Foundatio.TestHarness, 76 tests) covers round-trips, ids and group keys with any characters, case-sensitive headers, leases and `LeaseExpiresAt`, renewal, lost and stale leases, abandon and delays, cancellation, how long an empty receive waits and how soon a waiting one wakes, dead letters, fan-out, durable and instance subscriptions, sends to one subscription, deletion with a receiver open, competing receivers, batching, names, size limits, ordering across competing receivers, retries and concurrent senders, deduplication, and counts, plus end-to-end tests through the real client, handlers and dead-letter replay. Several tests use a second transport from `CreatePeerTransport` (by default another `CreateTransport()`) as another process on the same broker; override it if separate instances of your transport don't share state. Tests use unique names, so they can run in parallel against a shared broker, and delete the queues and subscriptions they open when they finish (call `DeleteAfterTest` for ones you create another way). Override `DeliveryTimeout`, `ShortLease` or `ShortDelay` for slow brokers, `StatsTimeout` (15 seconds) when your counts lag longer, and `WakeUpTimeout` (3 seconds) only if your broker cannot deliver to a waiting receive sooner than its own polling interval. Set `ShortLease` to the lease your broker grants if it decides leases itself, and override `StaleCompleteMayRemoveTheMessage` only if it cannot tell a stale receipt (see Lost leases). The count tests poll until the counts match, skip each check whose count you leave null, and skip a test whose source you return null for.

A provider outside this repo references the suite from the `Foundatio.TestHarness` package, the same version as the `Foundatio` it builds against, in an xUnit v3 test project. The suite targets .NET 10, and the provider API is experimental (see [below](#experimental-api)), so the test project suppresses FOUNDATIO0001 too:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);FOUNDATIO0001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Foundatio.TestHarness" Version="$(FoundatioVersion)" />
    <PackageReference Include="xunit.v3.mtp-v2" Version="4.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\MyCompany.Foundatio.MyBroker\MyCompany.Foundatio.MyBroker.csproj" />
  </ItemGroup>
</Project>
```

xUnit v3 runs on Microsoft.Testing.Platform: with the .NET 10 SDK, `dotnet test` needs `"test": { "runner": "Microsoft.Testing.Platform" }` in your `global.json`, and then takes `--project`, as in `dotnet test --project tests/MyBroker.Tests`.

The suite cannot see your broker's internals, so add tests of your own for what only your transport does: the topology it creates and removes, cleanup of a crashed process's instance subscriptions, how it maps names and ids, messages written by other producers, and its own limits. Every in-repo provider has a few.

## Job store

Implement `IJobStore` (10 methods). [View source](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio/Jobs/Store)

- `CreateAsync` (returns the job's id, or null if the id exists), `GetAsync`, `QueryAsync` (newest first, continuation token), `CountAsync`, `DeleteAsync`. Persist every field of `JobInfo`, including all `Headers` exactly as given (case-sensitive names, possibly empty values).
- `CreateAsync(job, availableAfter)`: what `IJobClient` calls for immediate and delayed jobs. Make the job available `availableAfter` after now on the database's clock rather than at `job.AvailableAt`, which comes from the producer's clock, and set `ScheduledFor` to that time when it is `default`. The default implementation uses `job.AvailableAt`, so a producer whose clock runs ahead of the others delays its jobs by the difference.
- `ClaimAsync`: atomically take the longest-due job among the requested names: queued and available, or running with an expired lease. Increment `Attempt`, issue a new `ClaimToken` and lease.
- `ClaimBatchAsync`: claim up to `maxCount` jobs at once, longest due first, each as `ClaimAsync` would and with its own `ClaimToken`. Workers call it to fill all their free slots, so a store with a network round trip should claim the batch in one call (one script, statement or transaction); the default implementation claims one job with `ClaimAsync`, which caps a worker at one job per round trip.
- `RenewAsync`, `ReportProgressAsync`, `CompleteAsync`: only for the current claim token. Completion is fenced by the token, not the lease, so a slow worker that wasn't replaced can still record its result.
- `RequestCancellationAsync`: queued jobs become cancelled; running jobs are flagged and `RenewAsync` reports it.
- Terminal jobs expire after `JobCompletion.RetainFor`; a zero `RetainFor` means reads stop returning the job at once. A job that succeeds drops its `Payload` and keeps the rest of its record; failed and cancelled jobs keep theirs.
- `JobInfo.ConcurrencyKey`: at most one running job per name and key. A claim skips a queued job whose key another running job holds (a running job whose lease expired still holds it), leaving it queued with its attempt unchanged, and a batch never takes two jobs with one key. Any `CompleteAsync` or a delete releases the key. Skipped jobs must not be passed over on every claim, or a thousand jobs waiting for one key starve the rest: set them aside until the key is released, as both stores here do.
- `JobInfo.DeduplicationKey`: while a job with the same name and key hasn't been claimed, cancelled or deleted, `CreateAsync` creates nothing and returns that job's id. The check and the insert are one atomic step.

A SQL store does these with columns and partial unique indexes. For the concurrency key, a `concurrency_key` column and `UNIQUE (name, concurrency_key) WHERE state = 'running'`; the claim picks rows with `NOT EXISTS (SELECT 1 FROM jobs r WHERE r.name = j.name AND r.concurrency_key = j.concurrency_key AND r.state = 'running' AND r.id <> j.id)` and `DISTINCT ON (name, concurrency_key)` for a batch, under `FOR UPDATE SKIP LOCKED`, and retries a claim that a concurrent one makes violate the index. To keep many waiting rows from slowing every claim, flag them `waiting` when a claim skips them, leave them out of the claim's index, and clear the flag of the oldest one when the key is released. For the deduplication key, a `dedupe_key` column, a `dedupe_pending` flag cleared on the first claim, cancel or delete, and `UNIQUE (name, dedupe_key) WHERE dedupe_pending`; insert with `ON CONFLICT DO NOTHING RETURNING id`, and when nothing is returned select the pending row's id, inserting again if it was claimed in between. A zero retention can delete the row on completion.

Use the database's clock for "now" so workers with skewed clocks agree. Throw database and connection errors as `JobStoreException` (in `Foundatio.Jobs`), with the client's exception as its inner exception, so apps calling `IJobClient` catch one type on every store, as transports throw `MessageBusException`; keep `ArgumentException` for invalid arguments and let `OperationCanceledException` through. Prove it with `JobStoreTestsBase`, exactly like the transport suite.

## Registration helpers

Give apps a `Use*` extension on `FoundatioMessagingBuilder` or `FoundatioJobsBuilder` that calls `UseTransport(factory)` or `UseStore(factory)`, so the service provider creates and disposes your transport or store. In the factory:

- Fill a logger factory left unset from the service provider. Fill a clock left unset from `IOptions<MessagingOptions>` (`TimeProvider`) for a transport, or `IOptions<JobWorkerOptions>` for a store, then the registered `TimeProvider`: apps and the test harness set those to keep Foundatio on one clock while tests move the app's.
- Use a client the app registered (a connection, an SDK client) when the options don't say how to connect, and leave it open on dispose; dispose only what you created.
- Offer a `Use*(connectionString)` overload for the common case.

## Experimental API {#experimental-api}

The contracts on this page are marked `[Experimental("FOUNDATIO0001")]`: `IMessageTransport`, `IMessageReceiver`, `IJobStore`, the types only they use (`IncomingMessage`, `OutgoingMessage`, `MessageDestination`, `ReceiverOptions`, `MessageSize`, the lease and receiver exceptions, `JobInfo`, `JobClaimRequest`, `JobCompletion` and `JobLeaseStatus`) and the conformance suites. They have been tried against Azure Service Bus only on its emulator, so they may change in a minor release, and a provider may need updating when you upgrade Foundatio. The app-facing API (`IMessageClient`, `IJobClient`, handlers, jobs, the builders and `Use*` helpers, `IMessageAdmin` with `MessageSourceStats`, `IWorkMonitor`, `FoundatioTestHarness`) is not affected and never reports the diagnostic.

Implement the contracts explicitly (`Task IMessageTransport.SendAsync(...)`), as the in-repo transports and stores do, so your public class shows only its constructors, options and `Capabilities`. A public `SendAsync` or `ClaimAsync` on a class that isn't experimental would let app code call the SPI through `var` without the diagnostic, and make its signature part of your stable API. Tests call the members through the interface.

Any reference to one of these types is a compile error, FOUNDATIO0001, until you suppress it. Suppress it for the whole provider project and its test project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);FOUNDATIO0001</NoWarn>
</PropertyGroup>
```

Or suppress it for one use with `#pragma warning disable FOUNDATIO0001`.

