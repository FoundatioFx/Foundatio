# Quorum Queue Migration

Quorum queues replicate their data across broker nodes; classic queues are node-local. This guide describes a deliberate topology change, **not** an upgrade to RabbitMQ 4.3. The companion provider work is tested on **4.2.5**.

::: warning Coordinate with the provider release
The retained-terminal and strict-delivery options below describe [Foundatio.RabbitMQ PR #100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100), not a claim about released 13.0.4 behavior. Review the [implementation and adoption contract](rabbitmq.md) before deploying a package that includes those changes. Merely enabling quorum queues does not select the required acknowledgement or publication modes.
:::

## Why migrate?

Quorum queues can improve queue availability when a majority of their replicas remains healthy. They add replication and broker-managed redelivery limits, not sharding of one queue across independently writable partitions or an exactly-once guarantee. Leader changes, connection recovery, unsafe retention policies, and unconfirmed publications still affect availability and data safety.

Choose according to required availability, workload, storage/network cost, and operational capacity. Do not promise zero downtime or no message loss from queue type alone. Native 4.3 delayed-retry features are not part of this 4.2.5 plan; quorum priorities on 4.2.5 use normal/high tiers.

## Configure a new logical subscription

The following configuration fragment assumes `connectionString` is an application-provided `amqps` URI and the new durable terminal exchange/queue/binding already exists. Every host alias must match a trusted certificate.

```csharp
using Foundatio.Messaging;

await using var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString(connectionString)
    .Hosts("node1.example:5671", "node2.example:5671", "node3.example:5671")
    .Topic("process-events")
    .SubscriptionQueueName("processor-events-quorum")
    .IsDurable(true)
    .UseQuorumQueues()
    .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
    .PublisherConfirmsEnabled(true)
    .RequirePublishRouting()
    .RequireSuccessfulDispatch()
    .DeliveryLimit(5)
    .PrefetchCount(10)
    .DeadLetterExchange("events-quarantine", "processor", DeadLetterStrategy.AtLeastOnce)
    .OverflowBehavior(QueueOverflowBehavior.RejectPublish));
```

Use `Hosts` for endpoints; comma-separated hosts in one AMQP URI are not the provider's host-list syntax. The prefetch value is an example to tune, not a quorum-specific recommendation. Mandatory publication proves at least one current route, not every expected subscription.

`UseQuorumQueues()` sets `x-queue-type=quorum`, nonexclusive/non-autodelete flags, and a broker delivery-limit argument. It does not force `IsDurable` back to true if the caller disabled it. Preserve explicit queue-type agreement between client configuration and the actual broker queue.

The finite broker budget can be exhausted by connection-loss redeliveries independently of application failures. Its at-least-once dead-letter strategy requires RejectPublish overflow, a configured exchange, a durable destination, and broker prerequisites. Client terminal publication is a different confirmed handoff. A deliberate alternative is an explicit raw broker limit of `-1L` with a finite application budget; see [budget selection](rabbitmq.md#quorum-broker-and-application-budgets). Do not disable a production limit implicitly.

## Queue identity and property equivalence

`Topic` names the exchange. **`SubscriptionQueueName` names the queue.** Changing the topic is not a queue-renaming operation.

An existing classic queue cannot be converted to quorum in place. Declaring it with incompatible properties can close the channel. A virtual host's default queue type affects new declarations; it does not convert existing queues and is not set by a queue policy containing `x-queue-type`. This provider should use explicit queue types so its retry strategy matches reality.

Do not rely on relaxed property-equivalence settings to hide a mismatch: they do not change stored data or guarantee that the provider's configured queue type matches the existing queue. Broker migration/relaxed-equivalence mechanisms require a separate reviewed plan; they are not a substitute for creating compatible topology.

## Controlled cutover

For required events, prefer a new durable queue with a distinct subscription name and a rehearsed transfer/cutover boundary.

1. Inventory queue/exchange names, bindings, actual queue types, replicas, TTL/overflow/dead-letter policies, producer confirmations, and consumers. Provision and verify the terminal destination.
2. Establish a producer pause or durable outbox boundary so events cannot fall into a gap while topology changes. Keep the original queue and data until reconciliation proves it is safe to remove them.
3. Create and bind the new quorum subscription. Move the old backlog with a reviewed confirmed transfer mechanism; ensure source acknowledgements follow accepted transfers. A Shovel can be one component of that plan, but its settings and interruption behavior must be verified.
4. Reconcile stable event IDs and allow for duplicates. Binding both old and new queues to the same fanout exchange produces copies in both; it is not transparent single-copy cutover.
5. Enable the new consumers and resume producers at the agreed boundary. Verify new traffic and backlog drain, quorum health, actual receipt, and idempotent side effects.
6. Keep an explicit rollback/reconciliation plan. Retire the old queue only after its required work and the cutover interval are accounted for and deletion is authorized.

Blue-green vhosts, Federation, or deleting/recreating a disposable queue are alternative approaches with distinct loss/duplication and routing boundaries. They are not automatically zero-downtime or safe for required events. Do not use destructive deletion as the default migration recipe.

## Compatibility on 4.2.5

| Setting | Consideration |
|---------|---------------|
| Durability | Use durable quorum queues; a durable flag alone does not replace confirmations or a retention policy. |
| Exclusive / auto-delete | Incompatible with quorum topology; use stable nonexclusive, non-autodelete subscriptions. |
| `x-max-priority` | Classic-only provider configuration; quorum's two priority tiers do not need this opt-in. |
| Global QoS | Use per-consumer QoS for quorum queues. |
| Queue-mode/lazy arguments | Do not copy classic-only queue-mode settings into quorum declarations. |
| Native delayed retries | Not enabled by this 4.2.5 migration; do not silently replace initial-message scheduling with retries. |
| Consumer timeouts | The provider's `ConsumerTimeout` option is guarded on this baseline. Review actual broker acknowledgement-timeout policy separately. |

A broker can close a channel or exhaust its budget while the client intentionally retains an unacknowledged delivery. Operational handling must account for those independent limits rather than restart-looping a blocked consumer.

## Verification

Observe the actual queue type and membership through RabbitMQ management/CLI tooling appropriate to the deployed version. Establish a healthy baseline before stopping the node or client path actually in use.

Verify majority availability where required, connection and consumer recovery, all admitted/confirmed event IDs, duplicates, terminal retention, and eventual progress. Endpoint relay tests do not prove replicated storage survives node loss, and passing startup is not evidence of completed business processing. Measure workload latency/backlog drain rather than assuming replication improves throughput.

Use the [provider verification guide](rabbitmq-verification.md) for the normal test suite. Migration and production rollout need a separate staging rehearsal; the PR does not execute a queue conversion or broker upgrade.

## References

- [RabbitMQ implementation](rabbitmq.md)
- [Messaging](../messaging.md)
- [RabbitMQ 4.2 quorum queues](https://www.rabbitmq.com/docs/4.2/quorum-queues)
- [RabbitMQ 4.2 virtual hosts](https://www.rabbitmq.com/docs/4.2/vhosts)
- [RabbitMQ 4.2 confirms](https://www.rabbitmq.com/docs/4.2/confirms)
- [RabbitMQ Shovel](https://www.rabbitmq.com/docs/shovel)
