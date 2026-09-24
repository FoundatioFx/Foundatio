# Messaging and jobs design decisions

The unreleased PR is revised around worker queues, explicit pub/sub subscriptions, and optional durable jobs. The current public guides are [Messaging](../guide/messaging.md) and [Durable jobs](../guide/jobs.md).

## Implemented

- Explicit queue consumers and event subscribers. Stable durable subscription names; renewable temporary leases for memory and Redis; named subscriptions required on AWS.
- Endpoint concurrency, duplicate-handler validation, and standalone disposable receive/settle.
- Broker-confirmed settlement, original preservation when dead-letter parking fails, lease supervision, and cancellation on lost ownership.
- Stable application IDs, distinct broker IDs and receipts, serialization metadata, allowlisted polymorphism, and partial batch outcomes.
- Consistent topology policy, producer declarations without phantom subscriptions, and fresh-instance AWS resource validation/deletion.
- One worker and persisted retry state machine for ad hoc and CRON jobs, with typed argument contracts and unique claim tokens.
- Atomic occurrence admission, node/type eligibility, fair due claims, stale recovery, and ownership-guarded progress/renewal/completion.
- Serializable schedule definitions with revisions and deployment configuration versions that preserve operator edits across restarts.
- Explicit consumer, worker, scheduler, and delayed-message dispatcher hosting. Registering clients or storage starts no background work.
- Bounded monitoring pages, independent active/history/idempotency/dispatch budgets, configurable retention, payload limits, and atomic capacity rejection.
- Atomic Redis receive/reclaim/settle, orphan pending-entry recovery, safe topic retention, and non-destructive per-subscription dead-letter inspection/replay.
- Dedicated CI services for Redis and SQS/SNS LocalStack conformance; executable local and distributed samples.
- Updated public guides, migration guidance, capability matrix, and repository skill.

- Consistent messaging/job feature builders, service-based durable subscription defaults, and combined wire-name/route registration with startup diagnostics.
- Supervised subscription renewal and recreation, observable listener health, hybrid-cache resynchronization, and immediate return of owned unsettled messages at shutdown.
- Independently replenished job slots, scoped job disposal, configurable persisted retry policies, delayed enqueue, completion waits, and separate success/failure diagnostics.
- Stable per-node schedule identity, expiry for unclaimed occurrences on retired nodes, cached CRON parsing and confirmed occurrence materialization.
- Indexed send outcomes and individual application IDs, native AWS batches and bounded Redis pipelines, per-entry malformed-envelope quarantine, and amortized safe retention.
- Measured header/routing/polling improvements, reusable hot-path benchmarks and runtime health/capacity reporting.

## Boundaries

Delivery and execution are at least once. Business idempotency and transactional outbox/inbox integration remain application responsibilities. Redis durability depends on deployment persistence and availability settings. LocalStack conformance does not certify live AWS behavior.

The changes intentionally break the unreleased API and Redis state layout. Do not mix old and new runtime binaries or reuse old experimental Redis namespaces; provision an isolated namespace when testing this revision.

The full external-provider workspace solution references Aliyun, Azure Service Bus, and Minio projects that are absent from this checkout. Its build cannot start. This is an environment limitation, separate from the successful in-repository validation below.

## Validation of the feedback changes

- Full in-repository solution rebuilt successfully. The existing sample AppHost emits ASPIRE010 because AspireUseCliBundle is false; there are no compilation errors.
- Core suite: 2,029 tests, 2,017 passed and 12 skipped; zero failures.
- Redis suite: 60 tests, 56 passed and four unsupported-capability skips; zero failures.
- AWS suite: 29 tests, 21 passed and eight unsupported-capability skips; zero failures.
- Redis 8.6 and SQS/SNS through LocalStack 3.8.1 ran in isolated local containers. This does not certify live AWS behavior.
- Documentation site build and git whitespace checks passed.
- Quickstart `--verify` passed: producer-only registration, command processing, durable event subscription, delayed typed job, persisted cancellation, automatic CRON execution and graceful shutdown.
- [Measured costs and repeatable benchmarks](https://github.com/FoundatioFx/Foundatio/blob/feat/messaging-jobs/benchmarks/MESSAGING_JOBS_BENCHMARK_RESULTS.md) cover header allocation, idle polling independent of retained history, batching and overlapping handlers. Timing results are local development evidence, not deployment capacity limits.

The six reproduced execution/ownership regressions are retained as tests. Additional shared cases cover retained-history pressure without losing idempotency, retry policy persistence, nonretryable failure, per-node expiry, payload/dispatch budgets, and dispatch lease timing. Recovery and configuration tests cover transient/lost subscriptions, cache gaps, cancellation, indexed batch results and startup wire-name collisions.
