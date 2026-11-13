# Messaging and durable jobs: design

An alternative to PR #533, built from the review of that PR. Goals, in priority order:

1. **A transport or job store is easy to write**, and a conformance suite proves it works.
2. **A fantastic developer experience**: each common task needs a handful of concepts, and mistakes fail early with clear errors.
3. **Nothing breaks.** `IQueue<T>`, `IMessageBus` and the existing jobs stay as they are, so providers and apps move one piece at a time.

Decisions: side-by-side with the legacy APIs for v14 (delete them in v15); store-based durable jobs; core owns handler registration (Mediator can build on it); pub/sub works on every broker, and queues are expressible on Kafka through the same contract.

## Layers

```
  Application        IMessageClient   IMessageHandler<T>   IJobClient   IJobHandler<TArgs> / IJobHandler
  ───────────────────────────────────────────────────────────────────────────────────────
  Foundatio core     routing · serialization · retries · dead letters · concurrency · leases
                     delays · shutdown drain · tracing · metrics · CRON · startup gate
  ───────────────────────────────────────────────────────────────────────────────────────
  Provider SPI       IMessageTransport + IMessageReceiver (9 methods)    IJobStore (10 members)
```

The provider SPI is `[Experimental("FOUNDATIO0001")]` in v14, because it has been tried against Service Bus only on its emulator, and not yet against RabbitMQ, and may change in a minor release. That covers `IMessageTransport`, `IMessageReceiver`, `IncomingMessage`, `OutgoingMessage`, `MessageDestination`, `ReceiverOptions`, `MessageSize`, the lease-lost and receiver-faulted exceptions, `IJobStore` with `JobClaimRequest`, `JobCompletion`, `JobLeaseStatus` and `JobInfo`, and the conformance suites. The diagnostic is an error by default, so provider projects suppress it with `NoWarn`. App code that uses the builders, the provider helpers, the clients, the handlers and the test harness never sees it. `TransportCapabilities` (the harness and the in-memory transport take it), `MessageSource` (`IMessageAdmin` takes it) and `MessageTooLargeException` (apps catch it) stay unmarked.

Messaging and jobs are independent: neither depends on the other's contracts. They interact only through ambient correlation (a job enqueued from a handler inherits the message's correlation id, and vice versa).

## Transport contract

| Member | Purpose |
|---|---|
| `Capabilities` | What the broker does natively. Core adapts. |
| `SendAsync(destination, messages)` | To a queue, to a topic's subscriptions, or to one durable subscription. Batch-first. |
| `CreateReceiverAsync(source, options)` | Queue, durable subscription, instance subscription, or a dead-letter queue. |
| `DeleteAsync(source)` | Remove a queue or subscription (with its dead letters), or only a source's dead letters. |
| `GetStatsAsync(source)` | Approximate queued, delayed, in-flight and dead-lettered counts for dashboards; each null when the broker can't tell. |
| `ReceiveAsync(max, maxWait)` | Leased deliveries. |
| `CompleteAsync` / `AbandonAsync(delay)` / `DeadLetterAsync(reason)` / `RenewAsync` | Settlement. |

A transport whose broker decides the lease (Service Bus sets a lock duration per entity) sets `IncomingMessage.LeaseExpiresAt`, and core renews a third of the way through the remaining lease instead of by its own `LeaseDuration`.

Everything else is core: there are no optional capability interfaces to discover, no push/pull split, no provisioning API and no outcome model in the SPI; the only read-side member is `GetStatsAsync`, whose counts are data the transport reads from the broker, never state it keeps for the purpose. `TransportCapabilities` (send, publish and retry delay limits, batch/message/lease limits, lease expiration, ordered groups, deduplication) is data, not interfaces, and the conformance suite reads it to decide which tests apply.

How brokers map:

| Broker | Queue | Topic / subscription | Instance subscription | Delay | Groups |
|---|---|---|---|---|---|
| Redis Streams | stream + group | fan-out into per-subscription streams | stream with heartbeat, swept when stale | sorted set | group hash: one outstanding message per group |
| SQS / SNS | queue | SNS topic → SQS queue | per-instance queue, tagged and swept | 15 min (longer sends emulated) | FIFO message groups |
| Service Bus | queue (`queue.{name}`) | topic subscription, with a rule for sends to one subscription | AutoDeleteOnIdle subscription, deleted on dispose | scheduled messages; retries are scheduled copies | sessions; delayed grouped messages held by their session's receiver |
| RabbitMQ | quorum queue | exchange → queue per subscription | exclusive auto-delete queue | delayed exchange, or none | — |
| Kafka | topic + consumer group, completion-tracked offsets | group per subscription, plus its own retry topic | every partition read from the end, no group | delay-tier topics and a forwarder | partition key |
| Storage Queues | queue | not supported | not supported | 7 days | — |

The Kafka transport sets `LeaseExpiration = false`, commits the lowest unfinished offset per partition, and copies abandoned messages to the source's own topic. Brokers with push client libraries buffer into a bounded channel inside the receiver.

Proof: `MessageTransportTestsBase` (68 tests) plus `JobStoreTestsBase` (31 tests). A provider test class is one override, plus `CreatePeerTransport` when a second transport over the same backend, which the suite uses as another process, needs different setup. The suite fails a transport that keeps group order only within one receiver, or that settles by message id and ignores receipts.

### Redis proof

A Redis Streams transport and a Redis job store (`src/Foundatio.Redis`, which came into this repo with its history in #603) were written against this contract, without changing core:

- **Transport:** about 1,060 lines of C# and 460 of Lua.
  - Each source is one stream and consumer group, plus sorted sets for leases and delays (scored by Redis `TIME`), and a hash for ordered groups.
  - Each source's keys share a hash tag, and every key a script touches is declared, so each operation is one atomic script under Redis Cluster.
  - Publishing fans out client-side into each subscription's stream, so a slow subscriber never blocks the publisher.
  - Idle receivers subscribe to a per-source pub/sub signal, published on every send, abandon and release, so delivery across processes takes about 1–2 ms; polling is the fallback.
  - Reads and delayed-message promotion stop at about 8 MiB per call. Dead letters can be bounded by `DeadLetterMaxLength` or `DeadLetterRetention`.
  - Instance subscriptions use heartbeats (every sixth of the timeout) and are swept when stale; a receiver that finds its subscription swept restores it and warns.
  - Redis must not evict the transport's keys (`maxmemory-policy noeviction`); a group whose outstanding message was evicted is released with a warning.
- **Job store:** about 410 lines of C# and 230 of Lua. One hash per job, sorted sets per name for queued and running jobs, and lexicographic indexes for queries. Every key is declared, so claims take two scripts (list candidates, then claim).
- **Results:** transport 78 tests (the suite plus Redis-specific ones) on a Redis 8 cluster, standalone Redis 8 and 6.2, and Valkey 8.1; job store 31/31 on a Redis 8 cluster and on Dragonfly.

The implementer's friction notes were folded back into the contract:

- Names are validated where they are created.
- These are now documented:
  - thread safety and what disposing the transport does;
  - that `DeliverAt` comes from the sender's clock;
  - deduplication scope and `EnqueuedAt`;
  - which fields `CreateAsync` keeps;
  - renewing after a lease expires but before another worker reclaims the job.
- `JobQuery.Validate` is public.
- The CRON overlap check is one read instead of a query.
- New conformance tests cover stale receipts, delivery count after a receiver is disposed, longest-due across names, renew-after-expiry and deleting a running job.

### AWS proof

An SQS/SNS transport was written in `src/Foundatio.AWS`, about 1,400 lines, also without changing core:

- **Mapping:**
  - queues → SQS queues;
  - topics → SNS topics with raw delivery into one SQS queue per durable subscription;
  - instance subscriptions → per-receiver queues, heartbeat-tagged, deleted on dispose and swept when stale;
  - dead letters → a per-source queue with 14-day retention, copy then delete so the reason is kept.
- **Leases:** visibility timeout; stale receipts map to `MessageLeaseLostException`.
- **Envelope:** headers, id, group and delivery time travel in one binary message attribute (SQS and SNS allow 10). A valid UTF-8 body is sent as-is; any other body travels in the attribute, which SQS counts by raw bytes, so nothing grows in transit. Foreign bodies are read as-is.
- **Names:** sanitized and hashed when changed, so distinct names never collide. Queue policies are merged, not overwritten.
- **Delays:** `MaxDelay` is 15 minutes; longer sends and delayed publishes are emulated in the receiver, which hides an early message within the 12 hours SQS allows from its receive. On a standard queue with 50,000 or more messages in flight, an early message is re-sent with a native delay instead of hidden.
- **Groups:** `Fifo = true` uses FIFO queues and topics and declares `OrderedGroups` and `Deduplication`. Standard queues still send `GroupKey` as `MessageGroupId`, for SQS fair queues.
- **Provisioning:** subscription and instance queues are created with their topic's policy.
- **Results:** the conformance suite passes on the Floci emulator, standard and FIFO. Floci does not enforce the 12-hour visibility rule, policy propagation delays or the in-flight quota, so tests simulate those rules; the transport has not yet run on real AWS. The existing SQSQueue and S3 suites still pass.

### Kafka proof

A Kafka transport was written in `src/Foundatio.Kafka`, with queues and pub/sub through the same contract:

- **Mapping:**
  - queues → a topic read by one consumer group from the earliest offset;
  - durable subscriptions → a consumer group per subscription on the topic, plus the subscription's own topic for retries and sends to that subscription;
  - instance subscriptions → every partition read directly from the end, with no consumer group, so a crash leaves nothing to sweep;
  - dead letters → a topic per source, never compacted and kept at least 14 days.
- **Settlement:** offsets are committed up to the lowest unfinished message per partition, so handling is concurrent. Abandoned messages without a group key are copied with an incremented delivery count; messages with one are released in place to keep group order. Their delivery counts and retry times, and the offsets finished behind a record held for its delivery time, are kept in a compacted `<prefix>.state` topic, so they survive rebalances, closes and (except the last couple of seconds) crashes.
- **Flow control:** a receiver whose buffer is full stops polling, apart from a keep-alive poll, and pauses partitions only past twice its limit, so a backlog drains at the handlers' pace (about 2,800 msg/s with 5 ms handlers, against 40 before). Records held for their delivery time are capped at 10,000 or 16 MB, beyond which their partitions are paused.
- **Delays:** unlimited, through shared delay-tier topics (1s to 6h) and a forwarder in every process with a receiver, so core never holds Kafka retries in-process. The forwarder only writes to the transport's own topics, drops a record its target keeps rejecting after 12 attempts, and restarts after a failure.
- **Declared:** ordered groups (partition key, and no message of a group handed out while an earlier one is unsettled). Not declared: lease expiration, deduplication. Adding partitions to a topic reorders groups, as documented.
- **Results:** the conformance suite passes on the classic and KIP-848 consumer group protocols.

### Service Bus proof

An Azure Service Bus transport was written in `src/Foundatio.AzureServiceBus` (which came into this repo with its history), about 2,100 lines of C# with comments, without changing core:

- **Mapping:**
  - queues → queues named `queue.{name}`, topics → topics named `topic.{name}`: Service Bus queues and topics share one namespace, and a Foundatio queue and topic may share a name (the suite's names test does);
  - durable subscriptions → topic subscriptions; instance subscriptions → a subscription per receiver with `AutoDeleteOnIdle` (5 minutes), deleted on dispose, so a crashed process's is removed by Service Bus;
  - one subscription as a destination → its topic, with the subscription's name in `x-foundatio-to`; every subscription the transport creates has a SQL rule matching messages without that property or with its own name, and an existing one with only the default rule gets this rule instead;
  - dead letters → each entity's own dead-letter queue, with the reason in `DeadLetterReason`; messages Service Bus dead-letters itself arrive with its reason and description.
- **Leases:** peek-lock, with the entity's lock duration reported in `LeaseExpiresAt`, the first transport to use it; it needed no change. Closing a receiver releases its locked messages without counting a delivery, so disposing abandons what it handed out first.
- **Envelope:** the body as-is and headers as application properties, so other Service Bus clients read both; the group key is the session id. Headers past 48 KiB of properties (Service Bus allows 64 KiB per message) move into the body, so the open `MaxHeaderSize` capability is not needed here. `MaxMessageSize` is the namespace's limit (256 KiB on Standard, an option for Premium) less 4 KiB for framing, the transport's properties, a retry's copy and the dead-letter reason.
- **Delays:** scheduled messages, unlimited, for sends and publishes. A delayed retry schedules a copy carrying the delivery count and completes the original: in one transaction on a queue, one after the other on a subscription (a transaction would span the topic and the subscription, which needs cross-entity transactions client-wide). Retries under a second are held by core (`MinRetryDelay`).
- **Groups:** `RequiresSession = true` declares `OrderedGroups`. A session receiver accepts sessions as it needs them (at most `MaxConcurrentSessions`) and receives no more of one while a grouped message it handed out is unsettled. Service Bus adds a scheduled message to its session only when it is due, so later messages would overtake it: a delayed grouped message is sent at once with its due time, and the receiver that gets its session holds it and the session, renewing the lock, until it is due. A grouped retry with a delay is held the same way and then abandoned, so it comes back first. Ungrouped messages share eight sessions and are received from them like a queue; a session per message would take an accept each and could not hold the 100 unsettled messages the batch test receives.
- **Deduplication:** `RequiresDuplicateDetection = true` declares it. The deduplication id is the Service Bus message id (hashed with the subscription's name for sends to one subscription, since a topic deduplicates for all its subscriptions), the Foundatio id travels in a property, and retry copies get fresh ids.
- **Provisioning:** queues, topics, subscriptions and rules are created through the administration client; `CreateEntities = false` leaves them to the app and then refuses instance subscriptions and deletes only dead letters. An existing entity in the other session mode is refused.
- **Results:** the conformance suite passes on the Service Bus emulator without sessions or duplicate detection, with sessions and with duplicate detection (66, 74 and 72 tests with the transport's own, none failing); each mode skips the capabilities it does not declare and the count tests, as the emulator reports no counts (the transport returns null there; the mapping is tested against a fake administration client). The legacy queue and message bus suites still pass. The transport has not run on real Service Bus.
- **Emulator gaps:** management on port 5300 only (the transport derives it from a `UseDevelopmentEmulator=true` connection string); `MaxDeliveryCount` at most 10; 50 queues per namespace and 50 subscriptions per topic, so each test gets its own prefix and deletes its queues and topics; `AutoDeleteOnIdle` on subscriptions ignored; runtime counts always zero; deleting a missing queue, or a subscription of a missing topic, answers Bad Request; `NOT EXISTS()` of a bracketed property name does not match, so the rule uses `IS NULL`; an abandon of an expired lock succeeds silently, so the transport checks the lease it knows of first.

### SPI refinements suggested by the provider work

Applied in the first round: name validation in the SPI types; the thread-safety, clock, dispose, dead-letter and `DeliveryCount` notes; `GroupKey` round-trip and delayed-publish tests; one-second quiet-period windows in the suite.

Applied in the second round:
- **Separate delay limits.** `MaxDelay` (sends), `MaxPublishDelay` and `MaxRetryDelay`, the last two defaulting to `MaxDelay`. SQS can now declare 15 minutes, unlimited (emulated) and 12 hours instead of one compromise value.
- **Size.** `MaxMessageSize` counts body plus UTF-8 header bytes, must hold after the transport's own encoding (binary bodies included), and transports throw `MessageTooLargeException` above it. The suite sends a message exactly at the limit.
- **FIFO batches.** A receive may return several in-order messages of one group. Core handles each group's messages in sequence and abandons the rest of the group if one is retried, so order survives retries. The suite checks that later messages are held while earlier ones are unsettled, and that an abandoned message comes back first.
- **One subscription as a destination.** `MessageDestination.Subscription(topic, name)`, used by dead-letter replay so a subscription's failures are not re-published to every subscriber.
- **Deletion.** `IMessageTransport.DeleteAsync(source)` for removed services' subscriptions, dead-letter purges and test cleanup.

From the Redis update, which also added ordered groups to Redis: the message id, group key and deduplication id count toward `MaxMessageSize` (`MessageSize.Of`) and are capped at 128 characters; group order is send order, delayed messages included; dead-letter sources are not ordered, so peeking shows a whole group; deduplication state may outlive a delete.

From the SQS update, which added FIFO mode: transports may shorten abandon delays to what the broker still allows for a delivery, and receivers left open across a delete may still see older messages (both documented). `MessageSize.Of` stays as it is; instead, a transport reserves framing for at least 100 headers and the dead-letter reason, keeps at least 1,024 bytes of a long reason, and the at-limit tests send 102 headers. Still open: a separate size limit for publishes (SQS accepts 1 MiB, SNS 256 KiB).

From the Kafka transport, as wording only: `CreateReceiverAsync` returns once the receiver can deliver (Kafka waits for its group join, or a first receive such as a dead-letter peek finds nothing); a new subscription gets messages published after it is created; a settle that throws must leave the message to be delivered again, also without lease expiration; `DeliveryCount` may restart after a crash on offset-based brokers, because Confluent.Kafka does not expose commit metadata. Then, also from Kafka: core calls `RenewAsync` about once a second on transports without lease expiration, so a handler whose partition moved is cancelled instead of finishing twice; `MinRetryDelay` lets a transport with coarse delays (Kafka's 1-second tier, SQS's whole-second visibility timeouts) have core hold shorter retries in-process, which stays durable because the delivery is still leased; and `MessageReceiverFaultedException` (or five receive failures in a row) makes core drain, dispose and replace a receiver.

From the Service Bus transport, without contract changes: `LeaseExpiresAt` worked as designed, and session receivers share one renewal per session, since all its messages share one lock. The provider guide now says to keep a queue and a topic of the same name apart where the broker's share a namespace, to check a known lease before an abandon where the broker accepts stale ones, and to abandon on dispose where closing a link does not count a delivery. Still open: core treats names as case-sensitive while Service Bus does not, so names that differ only in case share an entity (the transport documents it; core could reject such names); `ReceiverOptions.LeaseDuration` means nothing on a broker with per-entity locks, so its test class sets `ShortLease` to the entity's lock duration; a delayed retry or delayed grouped message that a transport holds with its group locked costs a session (Service Bus) or memory (Kafka) per waiting group, the same trade-off core-owned holds would face; and `DeleteAsync` never deletes topics, so test cleanup of a broker with entity quotas (the emulator's 50 queues) happens outside the SPI.

Not added: a maintenance hook. A transport already runs a timer for instance-subscription heartbeats, and sweeping stale ones from that timer (or on publish) needs no extra contract.

## Messaging semantics

- **One handler model.** `Task HandleAsync(T message, IMessageContext context, CancellationToken ct)`, the same shape as `IJobHandler<TArgs>`; both contexts extend `IHandlerContext` (`Attempt`, `CorrelationId`, `Headers`, `ReportProgressAsync`), so helper code can take either. Return completes; throw retries by `RetryPolicy`, then dead-letters; `context.DeadLetter(reason)` and `context.Retry(delay)` decide without throwing. Manual settlement exists only for transport authors.
- **Send vs publish** is the only topology choice. `AddConsumer` (queue, competing) or `AddSubscriber` (topic; `Scope = Service | Instance`).
- **Service names.** Durable subscriptions are named after the service, so on any transport but the in-memory one the host fails to start when a service subscriber has no `Name` and `UseServiceName(...)` isn't set. The application-name fallback is for the in-memory transport and the test harness only.
- **Shared subscriptions.** A service has one subscription per topic, and the handlers on it share each delivery. Handlers on the same source must agree on `RetryPolicy`, `LeaseDuration` and `MaxConcurrency` (unset differs from set, and policies compare by value, delegates included); the host fails to start otherwise, naming both handlers. `SubscriptionOptions.Name` gives a handler a subscription of its own, at any scope.
- **Options a transport ignores.** A `GroupKey` on a transport without `OrderedGroups`, or a `DeduplicationId` without `Deduplication`, logs a warning once per message type. `MessagingOptions.RequireTransportCapabilities` makes it throw instead.
- **Names** come from `[Message("...")]`, `MapMessage<T>()`, or the full type name; they are validated to `[A-Za-z0-9._-]`, and collisions throw. Only registered types are deserialized.
- **Routing uses the runtime type**, so `PublishAsync<Base>(derived)` reaches derived subscribers; base-type handlers receive mapped derived types on a shared topic.
- **Unknown types**: skipped on a subscription (topics carry many types); retried then dead-lettered on a queue (usually a rolling deploy). A published message lists its `[Message]` base types and interfaces in `message-base-types`, so a base-type handler receives derived types from services this one doesn't know.
- **Delays**: native up to `MaxDelay`; longer sends hop (re-sent when they arrive early); longer publishes throw rather than duplicate; no native retry delay, or one under `MinRetryDelay` → retries are held in-process, still leased.
- **Leases** renew from receipt until settlement (by `LeaseExpiresAt` when the transport sets it), including messages waiting their turn in a group batch, at a third of the lease (about once a second, as an ownership check, without lease expiration). A failed renewal is retried within what remains of the lease; renewal stops before a message is settled. A lost lease cancels the handler and skips settlement. A receiver that faults, or fails five receives in a row, is drained and replaced.
- **Dead-letter failures**: when the transport refuses a dead-letter, the redelivered message retries only the dead-letter, without running handlers again, and health reports it. Reasons are capped at 1,024 UTF-8 bytes.
- **Shutdown** stops receiving, drains for `ShutdownTimeout` (25s), then cancels and releases what remains. Settlement never uses a cancelled token.
- **Hosting**: registering handlers is enough. Every `AddFoundatioMessaging` call accumulates; roles are decided when the host starts, not when a method is called. Disposing the service provider stops processors, the job worker and the scheduler even if the host was never stopped.
- **Tracing and metrics**: producer/consumer activities linked by `traceparent`; counters for sent, handled (by outcome), dead-lettered, unmatched, discarded; handling duration.
- **Provider helpers.** `UseRedis`, `UseSqs`, `UseKafka` and `UseAzureServiceBus` (and `UseRedis` for jobs) take options, a factory or, for Redis and Service Bus, a connection or clients. They fill an unset logger factory and time provider from DI, and the container creates and disposes the transport or store; a connection from DI stays the app's. A missing `IConnectionMultiplexer` fails host start.

## Job semantics

- **Contract-first**: `IJobHandler<TArgs>` is identified by its argument type, so producers enqueue `new GenerateReport(...)` without referencing the job class. A job without arguments implements `IJobHandler` and gets the same `IJobContext`.
- **Legacy jobs** run through one bridge, `LegacyJob<TJob>` (`AddLegacyJob<TJob>()`, `AddCronJob<LegacyJob<TJob>>`), named after the job class; a failed `JobResult` fails the attempt and a success message becomes the job's message. `EnqueueAsync<TJob>` and `AddCronJob<TJob>` require `IJobHandler`, and `AddJob<TJob>` rejects an `IJob` at registration, so the bridge can be made `[Obsolete]` together with `IJob`.
- **App view and store record.** `IJobClient` returns `JobDetails`, with `GetArguments<TArgs>()`; `JobInfo` in `Foundatio.Jobs.Store` is the store's record, part of the SPI.
- **Headers and tracing.** `EnqueueOptions.Headers` are stored with the job and passed to `IJobContext.Headers`. Enqueueing under an activity stores its `traceparent` and `tracestate`, and the job's activity continues that trace. `CorrelationId` defaults to the current message's, then the current job's, then `Activity.Current.Id`, as `QueueBase` does.
- **Claim token fencing.** A run's result is accepted unless another worker has claimed the job since, even if the lease expired. The worker also cancels a job shortly before its lease would run out when it can no longer renew it, and records no result for that run.
- **Names come from the arguments' runtime type**, so arguments passed as `object` or a base type run the job for their actual type.
- **The result is authoritative.** A job that finishes successfully succeeds even if cancellation was requested. Cancellation marks it cancelled only when the job stops because of it.
- **Shutdown releases** running jobs without consuming an attempt. Crashes (expired leases) do count, and a job that keeps crashing fails after `MaxAttempts`.
- **Idempotency only when asked**: a caller-supplied `JobId`. Random ids reserve nothing.
- **CRON without leaders or stored schedules.** Every instance computes the latest occurrence within the misfire threshold and creates `cron:{schedule}:{utc}`; the store keeps one. Each schedule is isolated in its own try/catch; overlap is skipped by default, checked against the previous occurrence in the store, so it holds across instances. Schedules are code, so removing one removes it.
- **Cancellation and progress** ride on lease renewal; progress writes are throttled. Message handlers report progress the same way; it goes to the monitor, a span event and a Debug log, never to the transport.
- **Store time** is used for leases, availability and retention.
- **Succeeded jobs drop their arguments** in both stores and keep the rest of the record until retention ends. Measured: a succeeded job takes about 1.6 KB in Redis, a failed one with 200 bytes of arguments about 2 KB, and an in-memory job about 1 KB.
- **Startup gate**: workers, schedulers and message handlers wait for Foundatio.Extensions.Hosting startup actions, however long they take, and do not start if they fail.

## Monitoring

A dashboard sees in-flight work for messages and jobs together, stats for all work, and opt-in progress, without a store write per message (PR #533's broker tracking doubled the I/O on every send and receive, and disagreed with the broker after crashes):

- **Tracker.** Every process tracks the work it runs, always on. A message or job shows as active once it has run past `ActiveThreshold` (1s) or reported progress; a capped buffer keeps recent visible work and every retry, failure, dead letter and cancellation; rolling one-minute buckets over `StatsWindow` (5 minutes) count every outcome and duration per message type and source or job name. Following one item costs about 90 ns and no allocation: the tracking fields live in the handler context. Payloads and headers are never recorded.
- **Snapshots on `ICacheClient`.** `AddFoundatioMonitoring(m => m.UseCache())` makes each instance write a versioned JSON snapshot every `SnapshotInterval` (5s), sooner when visible work changes, with an expiry of three intervals plus a membership entry, so a crashed instance drops out and a stopped one removes itself. Any shared cache works, so no store SPI was added; a hybrid or in-memory cache shows partial data.
- **Backlog.** `IMessageTransport.GetStatsAsync` reads approximate counts from the broker: Redis from the stream, group and lease, retry and delay sets in one script (exact at that moment); SQS from queue attributes (retries waiting out their visibility timeout count as in flight; FIFO has no delayed count); Kafka from consumer lag read without joining the group (no in-flight or delayed counts; a received message stays queued until its offset is committed); Service Bus from runtime properties (locked messages count as queued; subscriptions have no delayed count). A broker that can't tell two states apart counts a message in the nearest one and documents it. `IMessageAdmin.GetStatsAsync` exposes one source.
- **`IWorkMonitor`** reads instances, active and recent work and stats across instances, the backlog of every source they receive from, and job counts per name and state, each cached for one interval however many dashboards poll; `GetOverviewAsync` returns a dashboard page in one call. A process with no handlers or jobs only reads. The dashboard UI itself is not part of this PR.
- Deliveries through `AddLegacyMessageBus` are not tracked.

## Testing and health

- **Whole-app tests**: `services.AddFoundatioTestHarness()` swaps in the in-memory transport and job store and records through internal observers in the client, the processor and the worker, so outcomes are what core decided (completed, retried, dead-lettered, skipped), not transport calls. Waits are signalled by recordings rather than polled by tests. CRON fires only through `TriggerCronJobAsync`, because the misfire catch-up would otherwise run schedules at startup depending on the time of day. `FoundatioTestHarnessOptions.TransportCapabilities` makes the in-memory transport behave like the production one (no deduplication, no ordered groups, delay, batch and lease limits, no lease expiration); the conformance suite also runs against an SQS-standard-like and a Kafka-like in-memory setup.
- **Health**: each processor, the worker and the scheduler keep their state, when the current run of errors began and the last error. `MessagingStatus` and `JobsStatus` expose it, and Foundatio.Extensions.Hosting turns it into health checks: retried errors are Degraded; a stopped loop, failed startup, or errors lasting past `unhealthyAfter` (5 minutes) are Unhealthy.

## Compatibility

- No existing public type changed. `IQueue<T>`, `IMessageBus`, `JobBase`, `JobRunner` and hosted jobs are untouched, so v13 providers keep loading.
- `AddLegacyMessageBus()` serves the original `IMessageBus` from any new transport; it passes all 35 tests of the original `MessageBusTestBase`. It runs on a message processor, so its receiver is replaced when it faults and leases are renewed; like the original buses, it handles messages one at a time in order and runs a message's subscribers concurrently. `HybridCacheClient` and `CacheLockProvider` run on new transports unchanged.
- `IJob` and `JobBase` work as CRON and on-demand jobs; `[Job(Name)]` names them.
- New names don't collide with v13 code: the job lifecycle is `JobState` (the hosting package has a `JobStatus`), and `RetryPolicy` lives in `Foundatio.Resilience`. `tests/Foundatio.SourceCompatibility` compiles v13-style app code, with Polly and ASP.NET Core imported, against the new packages; it failed on both names before the rename. A file that imports `Foundatio.Resilience` together with `Polly.Retry`, `Quartz` or `Amazon.Runtime` and names a bare `RetryPolicy` still needs an alias; that one was kept.
- Nothing new depends on `IJob`, so v15 can delete the legacy types and `LegacyJob<TJob>` without renaming anything new.
- The new code's public members are documented, and `.editorconfig` turns CS1591 on for its files (legacy files stay exempt).
- Core gains `Microsoft.Extensions.Hosting.Abstractions` (8.0) and an internal copy of Cronos.

## Compared with PR #533

| PR #533 issue | Here |
|---|---|
| `IJobRuntimeStore` mixes job queue, broker tracking, delayed dispatch, CRON storage, counters | `IJobStore` is jobs only. No broker tracking; delays are transport-native or emulated; CRON needs no storage |
| About 155 public types; 18-member `IMessageBus`; about 7 settlement models; 11 `ISupports*` | About 87 new public types, including the test harness, health status, dead-letter admin and the work monitor; 5-member `IMessageClient`; one handler model; one SPI and a capabilities record |
| No group/partition/dedup key; #579 GroupId regresses | `GroupKey` and `DeduplicationId` on every send; `OrderedGroups` and `Deduplication` capabilities with conformance tests |
| In-repo `Foundatio.Redis`/`Foundatio.Aws` collide with published packages | Redis, AWS and Kafka move into this repo whole, with their history and unchanged package IDs (#603), so there is one source per package; the other providers stay in their own repos |
| Mediator duplicates handler registration and routing | Core owns registration and routing; Mediator can map its handlers to `AddConsumer`/`AddSubscriber` |
| Big-bang delete of queues and jobs | Side by side; legacy adapter; existing jobs reused |
| Shutdown redelivers handled messages | 25s drain; settlement on an uncancelled token (tested) |
| Delayed sends lost without the worker | No dispatcher; delays are native or emulated (tested) |
| Unrelated events on a shared topic retried 50 times | Skipped (tested) |
| Polymorphic publish never arrives | Routed by runtime type (tested) |
| One receive error marks the app unhealthy | Receive and claim errors back off inside the processor and report Degraded; Unhealthy only when a loop stops or errors outlast a threshold (tested) |
| Cancel racing success records Cancelled; shutdown during settle records Interrupted; completion needs an unexpired lease; shutdown uses an attempt | Result authoritative; release refunds the attempt; token-only fencing (all tested) |
| One bad schedule stops all CRON; 1-minute catch-up; editing a schedule crashes startup | Per-schedule isolation; configurable misfire threshold (1h default); schedules are code (tested) |
| 7-day idempotency reservation for every job caps throughput | Only caller-supplied ids are deduplicated |
| Jobs start before startup actions | `IStartupGate` (tested) |
| Handlers registered after `AddFoundatioWorker` never run | Roles resolved at host start; registrations accumulate (tested) |
| Hand-written 13-member fake to unit test a handler | `TestMessageContext`, `TestJobContext`; `AddFoundatioTestHarness()` for whole-app tests |

Size: about 10.4k lines of new core source including doc comments, which every public member has (Messaging 3.6k, Transport SPI and in-memory 1.4k, Jobs 2.7k, Monitoring 1.8k, test harness 0.9k), plus 2.7k lines of conformance suites. For scale, PR #533's core Messaging and Jobs folders alone add about 8.2k lines, without monitoring.

## Not done yet

- Providers beyond Redis, SQS/SNS, Kafka and Service Bus (RabbitMQ), plus conformance runs against real brokers in CI. The AWS and Service Bus transports have only run against emulators.
- Waking idle job workers in other processes when a job is enqueued. Today it is local wake-up plus a 1s poll. Retention for unread durable subscriptions (Redis warns when one looks orphaned).
- Held messages count as delivery attempts: when a grouped message is retried, the group's later messages in the batch are released and count the delivery. Fixing it needs a release that doesn't increment `DeliveryCount`, an SPI change.
- Mediator mapping onto `AddConsumer`/`AddSubscriber` and `IJobClient`.
- Ordering for transports without `OrderedGroups` is not emulated in core: it would only hold within one process, which is worse than an honest "unordered".
- Marking legacy types `[Obsolete]` once providers ship transports.
- One transport per app. Reserved for later, without SPI changes: named transports chosen per message type or queue (`UseTransport("fifo", …)` plus `MapMessage<T>(…, transport: "fifo")`), so one FIFO queue doesn't make every queue FIFO.
- Left until a RabbitMQ provider exists: a `MaxHeaderSize` capability and core-owned holds for delayed grouped messages. Service Bus needed neither (see its proof). Then the SPI can drop `[Experimental]`; until then, providers outside this repo need `NoWarn` FOUNDATIO0001.
