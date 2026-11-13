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
| One subscription | `MessageDestination.Subscription(topic, name)` delivers to that subscription only (used to replay its dead letters). Like a queue, it must not lose messages sent before a receiver exists. |
| Instance subscription | A copy for each open receiver; removed when the receiver is disposed or its process dies. |
| Dead-letter queue | `DeadLetterAsync` moves a message (id, body, headers kept, plus a reason header) to a queue receivable as `MessageSource.DeadLetter(source)`. A message at `MaxMessageSize` must fit with any reason: you may shorten the reason, keeping at least its first 1,024 UTF-8 bytes. Dead-lettering the same id again (after a replay) keeps both. |
| Deletion | `DeleteAsync` removes a queue or durable subscription with its messages and dead letters, or only the dead letters of a `DeadLetter` source. It succeeds when nothing exists and throws `ArgumentException` for instance subscriptions. Deduplication state may outlive it. You may refuse with `MessageBusException`, keeping the messages, while a receiver is open on the source in any process (Kafka does); otherwise a receiver still open on the deleted source must keep receiving new messages or throw from `ReceiveAsync` (preferably `MessageReceiverFaultedException`), never go silently deaf. |
| Leases | A received message is invisible for `LeaseDuration`; unsettled messages are delivered again with a higher `DeliveryCount`. If your broker decides the lease itself (a Service Bus lock duration is set per entity), set `IncomingMessage.LeaseExpiresAt` to when it ends, on this process's clock, in `ReceiveAsync` and again in `RenewAsync` when the broker extends it by its own duration. Foundatio then renews a third of the way through what is left of that lease, and bounds renewal retries by it, instead of following `LeaseDuration`. Only the transport writes it. |
| Lost leases | Once a lease is lost (it expired and the message went out again, its receiver or transport was disposed, or its partition moved), `RenewAsync`, `AbandonAsync` and `DeadLetterAsync` on it throw `MessageLeaseLostException` (or `ObjectDisposedException` after a dispose) and leave the newer delivery alone. `CompleteAsync` should too; only where the broker cannot tell a stale receipt (SQS) may it remove the message instead. Check the lease you know of before an abandon or dead-letter where the broker accepts a stale one silently (the Service Bus emulator abandons an expired lock without an error). A renew after the lease expired but before anyone else got the message may take the lease back or throw. |
| Bytes and headers | Body, headers and `GroupKey` delivered unchanged. Header names starting with `x-foundatio-` are reserved for your wire format: you may reject them with `ArgumentException` or pass them through. Ids, group keys and deduplication ids are 1 to `OutgoingMessage.MaxIdLength` (128) characters of any kind, spaces and non-ASCII included; map them if your broker is stricter. Names only contain letters, digits, `.`, `_` and `-`; shorten or hash them if your broker needs to, deterministically, from the source's own fields (not `ToString()`). A queue and a topic may have the same name: keep them apart if your broker's queues and topics share one namespace (Service Bus does). |
| Counts | `GetStatsAsync` reports a queue's or durable subscription's `Queued`, `Delayed`, `InFlight` and `DeadLettered` messages; see [Counts](#counts). |
| Size | Accept any message within `MaxMessageSize`, measured by `MessageSize.Of` (body, headers, message id, group key and deduplication id), binary bodies included, on queues and topics alike, and throw `MessageTooLargeException` above it. `MessageSize.Of` counts no per-header overhead: declare the limit after your own encoding overhead, reserving framing for at least 100 headers and the dead-letter reason. Split a batch that exceeds your broker's request size. |

All members may be called concurrently: settlement and renewal run while `ReceiveAsync` is waiting, and `ReceiveAsync` throws `OperationCanceledException` promptly when cancelled. Disposing the transport closes its receivers, and their unsettled messages go to the next receiver, in this process or another, with a higher `DeliveryCount`: where closing a link releases messages without counting a delivery (Service Bus), abandon them first. An instance subscription left behind by a crashed process is your responsibility to remove; a heartbeat with an expiry, swept from the same timer that sends your heartbeats (or on publish), works well. A settlement call that fails for another reason must leave the message to be delivered again within a bounded time: after its lease, or, if leases don't expire, after a short delay you choose (Kafka waits five seconds), never only when the receiver closes. When `SendAsync` throws, forget the deduplication ids of the messages it did not send, or the caller's retry is dropped. `CreateReceiverAsync` returns once the receiver can deliver: wait for the broker's assignment, such as a Kafka consumer group join, so the first receive sees messages already waiting.

Declare honestly what your broker does natively in `TransportCapabilities`:

- **Delays:** `MaxDelay` for sends, `MaxPublishDelay` for publishes and `MaxRetryDelay` for `AbandonAsync` (each: `null` unlimited, a limit, or `TimeSpan.Zero` for none; the last two default to `MaxDelay`). Foundatio emulates longer send delays, holds retries in-process when there is no retry delay or the delay is under `MinRetryDelay` (for coarse delays: SQS visibility timeouts are whole seconds), and rejects publishes beyond `MaxPublishDelay`. A held retry stays leased on the broker, so a crash only delivers it again sooner. SQS, for example, declares 15 minutes for sends and 12 hours for retries, and an unlimited publish delay because the transport hides early SNS deliveries until they are due.
- **Limits:** batch sizes, `MaxMessageSize`, `MaxLeaseDuration`.
- **`LeaseExpiration`:** false for connection-held deliveries such as RabbitMQ, or offsets such as Kafka. Foundatio still calls `RenewAsync` about once a second while handling; throw `MessageLeaseLostException` once a delivery has gone to another consumer (a revoked partition) and the handler is cancelled.
- **`OrderedGroups`:** `GroupKey` maps to SQS message groups, Service Bus sessions or Kafka partition keys. Deliver in send order (within one `SendAsync` call and across calls that complete one after another; concurrent senders may interleave), where a delayed message, or one abandoned with a delay, holds back its group's later ones. Never hand out a message of a group to any receiver, in any process, while an earlier one is unsettled. One receive may return several in-order messages of a group; Foundatio handles them in sequence and abandons the rest if one is retried. Dead-letter sources are not ordered.
- **`Deduplication`:** drop a repeated `DeduplicationId` sent to the same destination within your window, in the same batch or from another process. The same id on another destination is not a duplicate.

The suite skips tests for capabilities you don't declare.

When a receiver can never deliver again (a fatal client error), throw `MessageReceiverFaultedException` from `ReceiveAsync`: Foundatio lets in-flight messages finish, disposes the receiver and opens a new one. Other receive errors are retried with backoff, and after five in a row the receiver is replaced too.

Push-based client libraries (RabbitMQ consumers, the Service Bus processor) can buffer deliveries in a bounded channel inside the receiver and return them from `ReceiveAsync`. Derive from `IncomingMessage` to carry your receipt (delivery tag, receipt handle, stream id).

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

Start there. The suite (Foundatio.TestHarness, 68 tests) covers round-trips, ids and group keys with any characters, leases, renewal, lost and stale leases, abandon and delays, cancellation, dead letters, fan-out, durable and instance subscriptions, sends to one subscription, deletion, competing receivers, batching, names, size limits, ordering across competing receivers and retries, deduplication, and counts, plus end-to-end tests through the real client, handlers and dead-letter replay. Several tests use a second transport from `CreatePeerTransport` (by default another `CreateTransport()`) as another process on the same broker; override it if separate instances of your transport don't share state. Tests use unique names, so they can run in parallel against a shared broker, and delete the queues and subscriptions they open when they finish (call `DeleteAfterTest` for ones you create another way). Override `DeliveryTimeout`, `ShortLease` or `ShortDelay` for slow brokers, and `StatsTimeout` (15 seconds) when your counts lag longer. The count tests poll until the counts match, skip each check whose count you leave null, and skip a test whose source you return null for.

The suite cannot see your broker's internals, so add tests of your own for what only your transport does: the topology it creates and removes, cleanup of a crashed process's instance subscriptions, how it maps names and ids, messages written by other producers, and its own limits. Every in-repo provider has a few.

## Job store

Implement `IJobStore` (10 methods). [View source](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio/Jobs/Store)

- `CreateAsync` (false if the id exists), `GetAsync`, `QueryAsync` (newest first, continuation token), `CountAsync`, `DeleteAsync`. Persist every field of `JobInfo`, including all `Headers` exactly as given (case-sensitive names, possibly empty values).
- `ClaimAsync`: atomically take the longest-due job among the requested names: queued and available, or running with an expired lease. Increment `Attempt`, issue a new `ClaimToken` and lease.
- `RenewAsync`, `ReportProgressAsync`, `CompleteAsync`: only for the current claim token. Completion is fenced by the token, not the lease, so a slow worker that wasn't replaced can still record its result.
- `RequestCancellationAsync`: queued jobs become cancelled; running jobs are flagged and `RenewAsync` reports it.
- Terminal jobs expire after `JobCompletion.RetainFor`. A job that succeeds drops its `Payload` and keeps the rest of its record; failed and cancelled jobs keep theirs.

Use the database's clock for "now" so workers with skewed clocks agree. Prove it with `JobStoreTestsBase`, exactly like the transport suite.

## Experimental API {#experimental-api}

The contracts on this page are marked `[Experimental("FOUNDATIO0001")]`: `IMessageTransport`, `IMessageReceiver`, `IJobStore`, the types only they use (`IncomingMessage`, `OutgoingMessage`, `MessageDestination`, `ReceiverOptions`, `MessageSize`, the lease and receiver exceptions, `JobInfo`, `JobClaimRequest`, `JobCompletion` and `JobLeaseStatus`) and the conformance suites. They have not been tried against RabbitMQ yet, and against Azure Service Bus only on its emulator, so they may change in a minor release, and a provider may need updating when you upgrade Foundatio. The app-facing API (`IMessageClient`, `IJobClient`, handlers, jobs, the builders and `Use*` helpers, `IMessageAdmin` with `MessageSourceStats`, `IWorkMonitor`, `FoundatioTestHarness`) is not affected and never reports the diagnostic.

Any reference to one of these types is a compile error, FOUNDATIO0001, until you suppress it. Suppress it for the whole provider project and its test project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);FOUNDATIO0001</NoWarn>
</PropertyGroup>
```

Or suppress it for one use with `#pragma warning disable FOUNDATIO0001`.

