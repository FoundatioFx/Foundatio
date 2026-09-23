# Foundatio.RabbitMQ

`RabbitMQMessageBus` implements `IMessageBus` for RabbitMQ pub/sub. Delivery guarantees depend on acknowledgement mode, publication confirmation, subscription topology, broker policy, and application handling; the default is best-effort pub/sub.

::: warning Unreleased provider changes
This guide includes the delivery, lifecycle, and TLS changes in [Foundatio.RabbitMQ PR #100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100), reviewed against provider commit `7c1d47778779cbebd111efe0a6686721488c618d`. The new options and changed exhaustion behavior are not a claim about the released 13.0.4 package. Coordinate publication of these docs with the provider merge/release.

The implementation and test baseline is **RabbitMQ 4.2.5**. This work does not upgrade brokers to 4.3. The delayed-exchange plugin artifact is independently versioned 4.2.0. A compatibility pin is not a broker-security or support-lifecycle certification.
:::

[Provider source](https://github.com/FoundatioFx/Foundatio.RabbitMQ/tree/fix/99-tls-and-verification-foundation) · [Options](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/7c1d47778779cbebd111efe0a6686721488c618d/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBusOptions.cs) · [Verification guide](rabbitmq-verification.md)

## Installation and basic usage

```bash
dotnet add package Foundatio.RabbitMQ
```

The package command installs a released package, not the unreleased branch described above. This basic example uses existing APIs and a local development broker:

```csharp
using Foundatio.Messaging;

await using var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString("amqp://guest:guest@localhost:5672")
    .Topic("events"));

await messageBus.SubscribeAsync<OrderCreated>(order =>
    Console.WriteLine($"Order created: {order.OrderId}"));

await messageBus.PublishAsync(new OrderCreated(123));

public record OrderCreated(int OrderId);
```

Use `Foundatio.Messaging`, not `Foundatio.RabbitMQ.Messaging`. The builder uses fluent methods; alternatively pass a `RabbitMQMessageBusOptions` object. Register a shared bus for the application's lifetime rather than creating a connection per publication.

## Configuration and delivery contracts

| Option | Default | Contract in the companion implementation |
|--------|---------|-------------------------------------------|
| `ConnectionString` | Required | AMQP URI supplying credentials, virtual host, and transport scheme. |
| `Hosts` | `null` | Nonempty list replaces the URI endpoint; selection is randomized. |
| `IsDurable` | `true` | Durable exchange/queue declarations and persistent publications; does not alone create a durable logical subscription. |
| `SubscriptionQueueName` | Empty | Use a stable nonempty name for required retained work. |
| `IsSubscriptionQueueExclusive` | `true` | Restricts the queue to its connection. Set false for shared durable subscriptions. |
| `SubscriptionQueueAutoDelete` | `true` | Queue can be deleted after its last consumer disappears. Set false for retained work. |
| `AcknowledgementStrategy` | `FireAndForget` | Broker automatic acknowledgement. `Automatic` instead acknowledges after provider dispatch succeeds. |
| `PublisherConfirmsEnabled` | `false` | Wait for broker confirmation of ordinary publications, not consumer processing. |
| `RequirePublishRouting` | `false` | Enables confirms and mandatory routing for immediate publications; zero-route returns fail. Does not prove every expected fanout subscription exists. Rejects nonzero delivery delays. |
| `RequireSuccessfulDispatch` | `false` | Requires matching live handlers to complete; malformed, empty/null typed bodies and unsupported/unmatched types become terminal failures. Requires `Automatic`. |
| `RequireBrokerDelayedDelivery` | `false` | Rejects delayed publication when broker scheduling is unavailable, before creating a memory timer. Requires durable publications and confirms. |
| `DeliveryLimit` | `2` | Failed redeliveries after the initial attempt; `-1` allows unlimited retries. Broker redelivery budgets remain a separate concern. |
| `DiscardOnDeliveryLimit` | `false` | Explicit legacy discard when there is no typed terminal exchange; incompatible with required dispatch. |
| `PrefetchCount` | `0` | Bounds unacknowledged deliveries, not handler concurrency. No `BasicQos` is sent when both prefetch settings are zero, so broker defaults can apply. |
| `PublishRecoveryTimeout` | 10 seconds | Maximum network-recovery wait per publish attempt, not an end-to-end operation deadline. |
| `ShutdownTimeout` | 10 seconds | Bounds individual transport cleanup waits, not one deadline for every lock, callback, and resource. |

`Automatic` is Foundatio's name for acknowledgements after dispatch; it is not RabbitMQ's `autoAck` mode. `FireAndForget` uses the broker's automatic acknowledgement and is not protected by prefetch or retry retention in the same way. Return a task covering the actual business work; async-void or internally detached work cannot be protected by awaiting the handler.

### Required classic subscription

Provision the durable source subscription and quarantine destination before admitting required traffic. The `events-quarantine` exchange must route `processor` to the intended durable queue. Its TTL and overflow policies must preserve the required retention. Setting an exchange name does not create or bind that destination.

```csharp
using Foundatio.Messaging;

var options = new RabbitMQMessageBusOptions
{
    ConnectionString = connectionString,
    Topic = "events",
    SubscriptionQueueName = "processor-events",
    IsDurable = true,
    IsSubscriptionQueueExclusive = false,
    SubscriptionQueueAutoDelete = false,
    Arguments = new Dictionary<string, object?> { ["x-queue-type"] = "classic" },
    AcknowledgementStrategy = AcknowledgementStrategy.Automatic,
    PrefetchCount = 10,
    PublisherConfirmsEnabled = true,
    RequirePublishRouting = true,
    RequireSuccessfulDispatch = true,
    DeliveryLimit = 2,
    DeadLetterExchange = "events-quarantine",
    DeadLetterRoutingKey = "processor"
};

await using var bus = new RabbitMQMessageBus(options);
```

Here `connectionString` comes from application configuration. The example is a configuration fragment, not destination provisioning or an application transaction. Stable queue names identify logical subscriptions: replicas sharing one queue compete for work; independent logical subscribers need different queues.

Configure the actual queue type explicitly. The provider selects retry behavior from `Arguments["x-queue-type"]`; omission does not establish the virtual host's default. Do not redeclare existing queues with incompatible arguments or attempt an in-place classic-to-quorum conversion.

### Quorum broker and application budgets

`UseQuorumQueues()` sets the queue type, disables exclusive/auto-delete behavior, and supplies `x-delivery-limit`. Keep `IsDurable = true`; the helper does not override an explicitly disabled durability setting. Direct options supply the application `DeliveryLimit` as the broker argument only when a raw broker limit is absent. Explicit raw limits are preserved.

The broker can exhaust its delivery budget during connection-loss redeliveries independently of handler completion. Required work needs a deliberate choice:

- A finite broker budget with **at-least-once broker dead-lettering**, `RejectPublish` overflow, a configured DLX, and a provisioned durable destination. The typed options are `DeadLetterStrategy = DeadLetterStrategy.AtLeastOnce` and `Overflow = QueueOverflowBehavior.RejectPublish`; equivalent administrator policy needs separate verification.
- Explicit `Arguments["x-delivery-limit"] = -1L` to disable the broker budget while retaining a finite application `DeliveryLimit`. This removes the broker's poison/redelivery limit and requires monitoring.

Do not silently disable production policy. Changing declaration arguments can require migration. Administrative deletion, destructive TTL/overflow settings, unsafe broker dead-lettering, and permanent storage loss can remove work without a client ACK. See [quorum migration](rabbitmq-quorum-migration.md) before changing an existing topology.

## Retry and terminal handling

Classic retries publish through the default exchange to the **actual failed subscription queue**, not the original fanout exchange. Logical/root identity is preserved, the retry count increases, and the initial scheduling header is removed. Quorum retries below the application limit use broker-managed rejection/redelivery.

Retry and terminal publications use a dedicated **confirmed, mandatory** channel even when ordinary confirms are disabled. The source is acknowledged only after the transfer is confirmed without a return. Missing exchanges, unroutable returns, negative confirmations, and ambiguous results retain the original and retry the handoff with bounded backoff rather than rerunning its business handler.

Confirmation is not destination-policy validation or downstream processing. Replacement publication and source acknowledgement are not atomic. An interrupted or ambiguous transfer can produce multiple copies with the same event ID. Deduplicate per logical consumer and event ID; do not let a global record suppress an independent subscription.

| Terminal condition | Outcome |
|--------------------|---------|
| Typed `DeadLetterExchange` configured | Confirmed, mandatory transfer to the configured route, then source ACK. Destination provisioning is separate. |
| Destination missing, unbound, or rejecting publication | Keep the source unacknowledged and retry with backoff; readiness is false while blocked. Repairing the destination resumes transfer. |
| No typed terminal destination and discard disabled | Retain the original and report blockage. Configure a destination and deliberately recreate/resubscribe the bus, or perform authorized recovery/replay. Retention is not successful processing. |
| No destination and explicit discard enabled | Log the intentional discard and ACK. Not a required-event configuration. |
| Malformed or overflowing retry metadata | Apply the terminal policy rather than resetting the retry budget. |

Terminal copies preserve body and identity, clear the original expiration and scheduling delay, and add failure-type/original-routing metadata without exception text. Destination retention policies still apply. A raw or policy-only broker DLX does not configure the client's terminal route: set the typed `DeadLetterExchange` and `DeadLetterRoutingKey` for that transfer. Broker-triggered TTL, overflow, and delivery-limit dead-lettering have their own safety policy.

## Required dispatch and lifecycle recovery

Required dispatch snapshots matching live handlers and validates a typed body before invocation. Raw `IMessage` subscribers can intentionally inspect an unknown payload themselves; this is not automatic business-schema validation. A removed local subscription does not block another live matching handler. If all registrations disappear, the consumer is closed; retained unacknowledged work returns to the broker subject to the queue's lifetime and broker policy. Required subscriptions must therefore be nonexclusive and non-autodelete.

Multiple local matching handlers share one broker delivery. A retry can repeat a handler that already succeeded. Use idempotent handlers or separate durable subscription queues for independent retry boundaries.

A one-second maintenance loop repairs recoverable channel closure and consumer cancellation separately from the client's automatic network recovery. Failed initialization removes the new local registration and partial transport. Permanent declaration/permission faults require corrected configuration and an explicit subscription retry.

Transport-generation invalidation prevents late handlers from settling a replacement generation. The provider copies raw payload bytes because an uncooperative handler can outlive its transport callback. Shutdown signals cancellation and bounds individual cleanup waits, but cannot terminate arbitrary application code; apply a host-level overall deadline separately.

Use `IsSubscriptionReady` with `LastSubscriptionError` and `LastDeliveryError`. They describe local registration, transport/consumer state, and blocked deliveries, not broker queue depth or business completion. Monitor ready/unacknowledged counts, quarantine growth, alarms, and actual progress. Repeatedly restarting a blocked consumer can exhaust a separate broker budget; repair the cause before replaying retained IDs.

## TLS, hosts, and ports

Use `amqps` for encrypted transport; `amqp` remains deliberately plaintext. A nonempty `Hosts` list replaces the URI host while retaining its credentials and virtual host. Endpoint selection is randomized, not primary/secondary ordering. Do not put comma-separated brokers into one URI; use `Hosts`.

An explicit URI port applies to URI-only connections. A host-list entry without a port uses 5671 for `amqps` or 5672 for `amqp`, not a custom URI port. Duplicate host/port pairs are collapsed case-insensitively. Invalid ports, malformed entries, and nonempty lists with no usable endpoint are rejected. A null/empty list uses the URI; whitespace-only entries are ignored when a usable entry remains. Bracketed IPv6 can include a port, while bare IPv6 uses the scheme default.

Each actual endpoint hostname/IP must match a trusted server certificate; the URI certificate name alone is insufficient for different replacement aliases. IPv6 identity excludes brackets. Endpoint TLS options are independent and preserve the factory's protocol/revocation settings. Connections that relied on relaxed name validation can fail after this change. Correct certificates or aliases rather than weakening validation. This change does not introduce a production validation bypass or new client-certificate API.

## Delayed publication on 4.2.5

The provider can use the independently versioned `rabbitmq_delayed_message_exchange` 4.2.0 plugin on the 4.2.5 broker. A specific unknown-exchange-type response or an existing regular fanout topic establishes an unavailable scheduling path; other permission, declaration, and network failures propagate. Installation alone does not turn an existing regular fanout topic into a delayed exchange.

With `RequireBrokerDelayedDelivery`, an unavailable path rejects the publication before a memory timer is created. Use durable publications and publisher confirms for that mode. Without the requirement, the documented legacy fallback logs a warning and schedules in process memory; pending work can be lost on disposal or process termination.

The plugin holds scheduled work on one broker node and routes it later. Publisher-process independence does not imply replicated scheduling, protection from plugin removal/storage loss, or guaranteed future routing. Provision the future durable queue and binding, and use an outbox or independently durable scheduler when those failures must be covered. `RequirePublishRouting` checks immediate routing and rejects a nonzero delay; use separate appropriately configured publishers for these different contracts.

The current provider's native delayed-retry and `ConsumerTimeout` option paths are rejected on the 4.2.5 baseline. This describes the provider's API guards, not a claim that every broker-level timeout mechanism is unavailable. No 4.3 migration is part of this change.

## Priority and flow control

On 4.2.5, quorum queues have normal/high priority tiers, not strict ordering across 32 numeric levels. `MaxPriority` / `UseMessagePriority()` configures classic `x-max-priority`; it is not sent for explicitly configured quorum queues. RabbitMQ.Client 7.2.2 omits a zero-valued message priority, so the broker's omitted-priority default applies. Priority cannot recall deliveries already sent to consumers.

Prefetch limits outstanding unacknowledged deliveries, not concurrency or total process memory, and does not bound `FireAndForget` consumers. Supported global QoS shares a limit across consumers on a channel, not across the connection; quorum consumers need per-consumer QoS. Tune against actual handler latency and workload, not a universal count recommendation.

## Compatibility and adoption

The exhaustion default changes from silent discard to **retention**. Release/adoption notes must also cover subscription-local retries and stable IDs, strict endpoint identity/port validation, and the new opt-in dispatch/routing/delay requirements. Retention can deliberately pause processing until an operator repairs the destination or configuration.

Before rollout, inventory actual queue types/policies, provision terminal routes, validate certificate aliases, and rehearse recovery/replay with idempotent handlers. The provider cannot make database commits and publication atomic. Application outbox/inbox/reconciliation, capacity planning, broker security review, and deployment validation remain separate responsibilities.

## Further reading

- [RabbitMQ verification](rabbitmq-verification.md)
- [Quorum queue migration](rabbitmq-quorum-migration.md)
- [Messaging](../messaging.md) and [serialization](../serialization.md)
- [RabbitMQ 4.2 queues](https://www.rabbitmq.com/docs/4.2/queues)
- [RabbitMQ 4.2 confirmations](https://www.rabbitmq.com/docs/4.2/confirms)
- [RabbitMQ 4.2 quorum queues](https://www.rabbitmq.com/docs/4.2/quorum-queues)
- [.NET client recovery](https://www.rabbitmq.com/client-libraries/dotnet-api-guide)
- [Delayed-exchange plugin limitations](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/blob/v4.2.0/README.md)
