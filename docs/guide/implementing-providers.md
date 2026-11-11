# Building a Transport or Job Store

A provider implements a small contract and proves it by passing a conformance suite. Foundatio supplies everything else: serialization, routing, retries and backoff, dead-letter decisions, concurrency limits, lease renewal, delay emulation, graceful shutdown, tracing and metrics.

## Message transport

Implement `IMessageTransport` (3 methods) and `IMessageReceiver` (5 methods). [View source](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio/Messaging/Transport)

```csharp
public interface IMessageTransport : IAsyncDisposable
{
    TransportCapabilities Capabilities { get; }
    Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken ct = default);
    Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken ct = default);
    Task DeleteAsync(MessageSource source, CancellationToken ct = default);
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
| Topic + durable subscription | Each named subscription gets a copy and keeps it while no receiver is open. |
| One subscription | `MessageDestination.Subscription(topic, name)` delivers to that subscription only (used to replay its dead letters). Like a queue, it must not lose messages sent before a receiver exists. |
| Instance subscription | A copy for each open receiver; removed when the receiver is disposed or its process dies. |
| Dead-letter queue | `DeadLetterAsync` moves a message (id, body, headers kept, plus a reason header) to a queue receivable as `MessageSource.DeadLetter(source)`. |
| Deletion | `DeleteAsync` removes a queue or durable subscription with its messages and dead letters, or only the dead letters of a `DeadLetter` source. It succeeds when nothing exists and throws `ArgumentException` for instance subscriptions. Deduplication state may outlive it. |
| Leases | A received message is invisible for `LeaseDuration`; unsettled messages are delivered again with a higher `DeliveryCount`. |
| Bytes and headers | Body, headers and `GroupKey` delivered unchanged. Names only contain letters, digits, `.`, `_` and `-`; shorten or hash them if your broker needs to, deterministically, from the source's own fields (not `ToString()`). |
| Size | Accept any message within `MaxMessageSize`, measured by `MessageSize.Of` (body, headers, message id, group key and deduplication id), binary bodies included, and throw `MessageTooLargeException` above it. Declare the limit after your own encoding overhead. Ids and keys are at most `OutgoingMessage.MaxIdLength` (128) characters. |

All members may be called concurrently: settlement and renewal run while `ReceiveAsync` is waiting. Disposing the transport closes its receivers. An instance subscription left behind by a crashed process is your responsibility to remove; a heartbeat with an expiry, swept from the same timer that sends your heartbeats (or on publish), works well. Settling a delivery that has since gone to another receiver should throw `MessageLeaseLostException` and must not affect the newer delivery.

Declare honestly what your broker does natively in `TransportCapabilities`:

- **Delays:** `MaxDelay` for sends, `MaxPublishDelay` for publishes and `MaxRetryDelay` for `AbandonAsync` (each: `null` unlimited, a limit, or `TimeSpan.Zero` for none; the last two default to `MaxDelay`). Foundatio emulates longer send delays, holds retries in-process when there is no retry delay, and rejects publishes beyond `MaxPublishDelay`. SQS, for example, declares 15 minutes for sends and 12 hours for retries, and an unlimited publish delay because the transport hides early SNS deliveries until they are due.
- **Limits:** batch sizes, `MaxMessageSize`, `MaxLeaseDuration`.
- **`LeaseExpiration`:** false for connection-held deliveries such as RabbitMQ, or offsets such as Kafka.
- **`OrderedGroups`:** `GroupKey` maps to SQS message groups, Service Bus sessions or Kafka partition keys. Deliver in send order, where a delayed message holds back its group's later ones. Never hand out a message of a group while an earlier one is unsettled. One receive may return several in-order messages of a group; Foundatio handles them in sequence and abandons the rest if one is retried. Dead-letter sources are not ordered.
- **`Deduplication`:** drop a repeated `DeduplicationId` within your window.

The suite skips tests for capabilities you don't declare.

Push-based client libraries (RabbitMQ consumers, the Service Bus processor) can buffer deliveries in a bounded channel inside the receiver and return them from `ReceiveAsync`. Derive from `IncomingMessage` to carry your receipt (delivery tag, receipt handle, stream id).

### Prove it

```csharp
public class MyTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    protected override IMessageTransport CreateTransport() => new MyTransport(...);
}
```

That is the whole test class. The suite (Foundatio.TestHarness, 42 tests) covers round-trips, leases, renewal, abandon and delays, dead letters, fan-out, durable and instance subscriptions, sends to one subscription, deletion, competing receivers, batching, names, size limits, ordering and deduplication, plus end-to-end tests through the real client, handlers and dead-letter replay. Tests use unique names, so they can run in parallel against a shared broker. Override `DeliveryTimeout`, `ShortLease` or `ShortDelay` for slow brokers.

## Job store

Implement `IJobStore` (10 methods). [View source](https://github.com/FoundatioFx/Foundatio/tree/main/src/Foundatio/Jobs/Store)

- `CreateAsync` (false if the id exists), `GetAsync`, `QueryAsync` (newest first, continuation token), `CountAsync`, `DeleteAsync`.
- `ClaimAsync`: atomically take the longest-due job among the requested names: queued and available, or running with an expired lease. Increment `Attempt`, issue a new `ClaimToken` and lease.
- `RenewAsync`, `ReportProgressAsync`, `CompleteAsync`: only for the current claim token. Completion is fenced by the token, not the lease, so a slow worker that wasn't replaced can still record its result.
- `RequestCancellationAsync`: queued jobs become cancelled; running jobs are flagged and `RenewAsync` reports it.
- Terminal jobs expire after `JobCompletion.RetainFor`.

Use the database's clock for "now" so workers with skewed clocks agree. Prove it with `JobStoreTestsBase`, exactly like the transport suite.
