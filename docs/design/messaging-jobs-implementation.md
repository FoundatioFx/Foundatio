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
- Bounded monitoring pages, indexed Redis claims/queries, seven-day terminal retention, and atomic capacity rejection.
- Atomic Redis receive/reclaim/settle, orphan pending-entry recovery, safe topic retention, and non-destructive per-subscription dead-letter inspection/replay.
- Dedicated CI services for Redis and SQS/SNS LocalStack conformance; executable local and distributed samples.
- Updated public guides, migration guidance, capability matrix, and repository skill.

## Boundaries

Delivery and execution are at least once. Business idempotency and transactional outbox/inbox integration remain application responsibilities. Redis durability depends on deployment persistence and availability settings. LocalStack conformance does not certify live AWS behavior.

The changes intentionally break the unreleased API and Redis state layout. Do not mix old and new runtime binaries or reuse old experimental Redis namespaces; provision an isolated namespace when testing this revision.

The full external-provider workspace solution references Aliyun, Azure Service Bus, and Minio projects that are absent from this checkout. Its build cannot start. This is an environment limitation, separate from the successful in-repository validation below.

## Final validation

- `dotnet build Foundatio.slnx --no-restore`: passed, zero warnings and errors.
- `dotnet test --solution Foundatio.slnx --no-build`, with Redis and LocalStack configured: 2,043 tests; 2,020 passed, zero failed, 23 skipped for unsupported provider capabilities or existing benchmark/cache exclusions.
- Redis 8.6 and SQS/SNS through LocalStack 3.8.1 conformance passed. The dedicated CI workflow starts these services and supplies the connection settings; hosted GitHub execution has not been run for these local changes.
- .NET whitespace verification and `git diff --check`: passed. Multi-target formatter import conflicts were resolved and the final solution rebuilt successfully.
- Documentation site build: passed.
- Quickstart smoke test: command and event handlers executed, typed job reached 100 percent progress, CRON ticked, and the host shut down gracefully.
