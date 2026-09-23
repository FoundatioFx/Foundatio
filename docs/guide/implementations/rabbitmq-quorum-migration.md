---
title: RabbitMQ Quorum Queue Migration
---

# Quorum Queue Migration

This guide describes an **optional queue-topology migration on RabbitMQ 4.2.5**, not a broker upgrade. The repository baseline remains 4.2.5. A working classic deployment does not need to migrate solely to adopt the provider fixes.

::: warning Companion implementation
The application retry/terminal behavior referenced here accompanies [Foundatio.RabbitMQ PR #100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100). Check the installed provider version and [delivery-safety contract](./rabbitmq-delivery-safety.md) before adoption. A linked branch is not a released package or permission to change production topology.
:::

## Why migrate?

Quorum queues replicate state across their members and can continue operating with a majority available. They do not shard one hot queue into independent partitions, remove reconnection pauses, or guarantee zero loss for arbitrary publication, acknowledgement, retention, and storage policies. Classic queues on this baseline are node-local.

Choose quorum when replicated retention is required and its operational/resource tradeoffs fit the workload. Keep publisher confirms, acknowledgement mode, terminal routing, and idempotency decisions explicit. Native delayed retries from later broker versions are not available on the pinned baseline.

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
    .PrefetchCount(10));
```

Here `connectionString` is an `amqps` URI with the intended credentials/vhost, and all endpoint names must match trusted certificates. Do not place comma-separated hostnames inside a single AMQP URI. The example demonstrates topology selection, not a complete required-delivery policy; configure terminal handling and the broker budget as described below.

`UseQuorumQueues()` sets `x-queue-type=quorum`, disables exclusive/auto-delete, and supplies a broker delivery-limit argument. It does not force `IsDurable` back to true after an explicit false. Quorum retries below the application budget use rejection/redelivery; exhaustion and broker-budget enforcement have separate terminal paths. A delivery limit by itself does not create a DLQ or ensure safe transfer.

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

First inventory the actual source queue, consumers, bindings, ready/unacknowledged IDs, and retention policy. Provision the replacement with the intended quorum membership and terminal topology. Then choose a controlled overlap or a paused cutover:

- Binding both queues to the same fanout exchange copies new events to both. Plan deduplication and side-effect ownership before allowing both consumer groups to process them.
- Pausing publication and draining the old queue avoids deliberate overlap but introduces a planned pause. Verify in-flight work and ambiguous producer outcomes before changing bindings.

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

Follow the [quorum budget section](./rabbitmq-delivery-safety.md#quorum-broker-and-application-budgets) for a safe finite broker budget or a deliberate unlimited-broker/finite-application choice. Raw administrator arguments are not silently overridden. Provision and test the terminal destination before sending required events.

## Verification and rollback

Inspect actual queue types, configured/online members, leader, bindings, effective policies, and consumers using broker management tooling. Verify each intended event ID is completed, retained, or durably quarantined, and that retained work progresses after recovery. Test node loss separately from client-path loss, and confirm enough queue members remain available.

Rehearse rollback before cutover. Returning consumers to the old queue does not recover messages sent only to the replacement queue; retain a replay/transfer plan and stable IDs. Keep deletion and broker upgrades outside the provider's automatic recovery behavior.

## References

- [RabbitMQ provider and TLS configuration](./rabbitmq.md)
- [Delivery safety](./rabbitmq-delivery-safety.md)
- [Provider test verification](./rabbitmq-verification.md)
- [RabbitMQ 4.2 quorum queues](https://www.rabbitmq.com/docs/4.2/quorum-queues)
- [RabbitMQ 4.2 queue properties and defaults](https://www.rabbitmq.com/docs/4.2/queues)
