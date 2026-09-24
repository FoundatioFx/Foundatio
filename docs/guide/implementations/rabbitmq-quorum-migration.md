---
title: RabbitMQ Quorum Queue Migration
---

# Quorum Queue Migration

This guide describes an **optional queue-topology migration on RabbitMQ 4.2.5**, not a broker upgrade. The repository baseline remains 4.2.5. A working classic deployment does not need to migrate solely to adopt the provider fixes.

::: warning Companion implementation
The application retry/terminal behavior referenced here accompanies [Foundatio.RabbitMQ PR #100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100). Implementation reference: [`9c3b3b1`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/tree/9c3b3b1c035880a1f0e3a12b8f316d7ef939af2e). Check the installed provider version and [delivery-safety contract](./rabbitmq-delivery-safety.md) before adoption. A linked branch is not a released package or permission to change production topology.
:::

## Why migrate?

Quorum queues replicate state across their members and can continue operating with a majority available. They do not shard one hot queue into independent partitions, remove reconnection pauses, or guarantee zero loss for arbitrary publication, acknowledgement, retention, and storage policies. Classic queues on this baseline are node-local.

Choose quorum when replicated retention or at-least-once broker DLX is required and its operational/resource tradeoffs fit the workload. Both queue types support strict handler processing, provider-confirmed terminal handoffs, TLS, and transport recovery. Keep publisher confirms, acknowledgement mode, terminal routing, and idempotency decisions explicit. Native delayed retries from later broker versions are not available on the pinned baseline.

## Enabling quorum queues

```csharp
using Foundatio.Messaging;

await using var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString(connectionString)
    .Hosts("broker-a.example:5671", "broker-b.example:5671", "broker-c.example:5671")
    .Topic("events")
    .SubscriptionQueueName("processor-events-quorum")
    .IsDurable(true)
    .UseQuorumQueues()
    .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
    .PublisherConfirmsEnabled(true)
    .RequirePublishRouting()
    .RequireSuccessfulDispatch()
    .DeadLetterExchange("events-quarantine", "processor", DeadLetterStrategy.AtLeastOnce)
    .OverflowBehavior(QueueOverflowBehavior.RejectPublish)
    .PrefetchCount(10));
```

Here `connectionString` is an `amqps` URI with the intended credentials/vhost, and all endpoint names must match trusted certificates. Before starting, provision a durable `events-quarantine` exchange and destination bound with routing key `processor`, verify broker DLX prerequisites, and apply [bounded source/quarantine policies](./rabbitmq-delivery-safety.md#capacity-and-backpressure). Constructing this bus does not provision quarantine or impose a disk bound. Do not place comma-separated hostnames inside a single AMQP URI.

`UseQuorumQueues()` sets `x-queue-type=quorum`, disables exclusive/auto-delete, and supplies a broker delivery-limit argument. It neither converts an existing queue nor forces `IsDurable` back to true after an explicit false. Quorum retries below the application budget use rejection/redelivery; exhaustion and broker-budget enforcement have separate terminal paths. A delivery limit by itself does not create a DLQ or ensure safe transfer.

## Migration challenge

An existing queue cannot be converted to another type in place. Redeclaring incompatible properties can close the channel with `PRECONDITION_FAILED`. The exchange (`Topic`) and subscription queue (`SubscriptionQueueName`) are different identities: changing only `Topic` is not a queue rename.

Do not rely on relaxed queue-type equivalence to convert storage. Even where the broker accepts a redeclaration, the existing queue stays its original type. A provider configured for quorum can then choose behavior inconsistent with an actual classic queue.

## Migration approaches

### New subscription queue with controlled cutover

Retain the existing exchange and explicitly give the replacement subscription a new name:

```csharp
// Same exchange; a distinct logical queue for the planned migration.
o.Topic("events")
 .SubscriptionQueueName("processor-events-quorum")
 .IsDurable(true)
 .UseQuorumQueues();
```

### Maintenance-window procedure

For a cutover without deliberate fanout overlap:

1. Inventory the source queue, consumers, bindings, effective policies, quarantine, and expected event IDs. Rehearse rollback and record the intended new queue name, membership, finite broker budget, capacity, and restricted credentials.
2. Pause producers during an agreed maintenance window. Reconcile in-flight confirms and durable outbox entries. Stop or account for delayed/scheduled publications that may arrive after the pause; stopping producers alone does not drain the delayed plugin.
3. Leave old consumers running until ready and unacknowledged counts are zero and expected IDs are completed or durably quarantined. Repair blocked retry/terminal routes first. If work cannot drain, use a separately tested transfer/replay plan before proceeding.
4. Stop old consumers and verify they have gone. Provision the new quorum queue and durable quarantine with the intended policies and bindings. Change `SubscriptionQueueName` and explicit queue type together. Never redeclare the existing classic queue as quorum.
5. Remove the old fanout binding while publication remains paused. Start new consumers, verify actual queue type/members/policies/readiness, and send identified canary events. Confirm completion, terminal routing, and capacity rejection before resuming producers.
6. Reconcile all expected IDs and monitor ready/unacknowledged/quarantine growth after resuming. Preserve the drained old queue and cutover record until the rollback window closes; delete only after explicit operational approval.

Binding both queues to the same fanout exchange copies new events to both. If an overlap is intentionally chosen instead, establish consumer-scoped deduplication and side-effect ownership before both groups process traffic.

Existing backlog is not moved automatically when a new queue is bound. Use a separately tested transfer/replay procedure where needed, preserve identity, and account for duplicates and acknowledgements. Do not delete the old queue until expected IDs are reconciled and rollback no longer needs it.

### Delete and recreate

Reserve deletion for disposable queues or an approved, fully drained cutover. Stop publication and consumers, verify ready and unacknowledged work, account for scheduled/in-flight publication, then perform the authorized replacement. A deletion command is not a loss-safe migration recipe.

### Virtual-host default queue type

The broker can configure a default type for newly declared queues. Queue type is **not** set by a normal `set_policy` rule containing `x-queue-type`, and changing a default does not convert existing queues. This provider also selects retry behavior from explicit queue arguments, so configure a matching `x-queue-type` in the application rather than assuming an omitted argument is sufficient.

### Separate virtual host or blue-green deployment

A parallel environment still requires a tested transfer/cutover plan, duplicate handling, producer coordination, backlog reconciliation, and rollback. Neither replication nor Federation alone makes this a zero-downtime or exactly-once migration. Broker-version upgrades remain a separate change.

## Configuration differences on 4.2.5

| Area | Quorum requirement or boundary |
|---|---|
| Durability | Durable, nonexclusive, non-autodelete topology with a stable queue name. |
| Priority | Normal/high tiers. Do not send the classic-only `x-max-priority` argument or assume strict numeric ordering. |
| QoS | Per-consumer prefetch; do not use channel-global QoS for quorum. Tune from workload measurements. |
| Broker delivery limit | Can act on connection-loss redeliveries as well as processing failures. Choose its terminal policy deliberately. |
| At-least-once broker DLX | Requires `RejectPublish` overflow, a DLX/destination, and broker prerequisites; duplicates remain possible. |
| Native retry/consumer-timeout options | The provider options guarded for later quorum versions are rejected on this baseline. |

Follow the [quorum budget section](./rabbitmq-delivery-safety.md#quorum-broker-and-application-budgets): prefer a finite broker budget with at-least-once DLX and reject-publish. A raw `-1` broker limit is an expert opt-out, separate from the application budget. Direct-options raw limits are preserved; the builder's `UseQuorumQueues()` writes the current `DeliveryLimit`, so apply an intentional raw override afterward. Provision and test the terminal destination before sending required events.

## Verification and rollback

Inspect actual queue types, configured/online members, leader, bindings, effective policies, and consumers using broker management tooling. Verify each intended event ID is completed, retained, or durably quarantined, and that retained work progresses after recovery. Test node loss separately from client-path loss, and confirm enough queue members remain available.

Rehearse rollback before cutover. Returning consumers to the old queue does not recover messages sent only to the replacement queue; retain a replay/transfer plan and stable IDs. Keep deletion and broker upgrades outside the provider's automatic recovery behavior.

## References

- [RabbitMQ provider and TLS configuration](./rabbitmq.md)
- [Delivery safety](./rabbitmq-delivery-safety.md)
- [Provider test verification](./rabbitmq-verification.md)
- [RabbitMQ 4.2 quorum queues](https://www.rabbitmq.com/docs/4.2/quorum-queues)
- [RabbitMQ 4.2 queue properties and defaults](https://www.rabbitmq.com/docs/4.2/queues)
