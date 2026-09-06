# Messaging and jobs redesign

This unreleased redesign uses explicit queue consumers and event subscribers, with an optional durable job runtime. See [Messaging](messaging.md) and [Durable jobs](jobs.md) for current APIs, runnable examples, guarantees, and migration guidance.

The common setup is a message bus plus a handler. Add durable jobs only when persisted execution state, progress, cancellation, retries, or CRON schedules are needed. Use `AddFoundatioWorker(configure)` for a combined worker. Plain `AddFoundatio()` registers clients without starting execution; individual hosting methods support separate role deployments.

The transport handles bytes, metadata, receipts, and broker operations. The messaging core owns routing, serialization, retry policy, lease supervision, and settlement. The job store owns atomic admission, claims, fenced mutations, schedule definitions, and retention. Ad hoc jobs and CRON occurrences run through one worker state machine.

Delivery is at least once. Stable application IDs, idempotent business operations, and transactional outbox/inbox boundaries remain application responsibilities. The runtime does not promise exactly-once side effects or distributed transactions across a business database and broker.
