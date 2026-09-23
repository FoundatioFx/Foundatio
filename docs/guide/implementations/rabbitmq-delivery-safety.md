---
title: RabbitMQ Delivery Safety
---

# Delivery safety on RabbitMQ 4.2.5

::: warning Companion implementation
This guide describes [Foundatio.RabbitMQ PR #100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100), not a claim about every released provider version. Its source baseline is [`7c1d477`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/tree/7c1d47778779cbebd111efe0a6686721488c618d). Publish/adopt these contracts only with the matching implementation. The broker compatibility baseline remains **4.2.5**, with no 4.3 upgrade.
:::

## Select the contract explicitly

The defaults remain best-effort pub/sub: `FireAndForget` acknowledgement, ordinary publisher confirms disabled, and permissive type filtering. Required events need durable topology, suitable broker policies, and idempotent application handlers in addition to the options below.

| Option | Contract |
|---|---|
| `AcknowledgementStrategy.Automatic` | Acknowledge after dispatch returns successfully, rather than automatically when the broker sends a delivery. Permissive dispatch alone does not establish that a required handler ran. |
| `PublisherConfirmsEnabled` | Wait for broker confirmation of an ordinary publication, not confirmation of consumer processing. |
| `RequirePublishRouting` | Enable confirms and mandatory routing for immediate publications. A zero-route return fails; this does not check every expected fanout subscription. Nonzero delays are rejected. |
| `RequireSuccessfulDispatch` | Require matching live handlers to complete. Unexpected unmatched types and malformed/empty/null typed payloads use the terminal policy. Requires Automatic acknowledgements. |
| `RequireBrokerDelayedDelivery` | Reject delayed publication when broker scheduling is unavailable. Requires durable publication and confirms; never silently schedule in memory. This is not replicated scheduling or future-route verification. |
| `DiscardOnDeliveryLimit` | Explicitly discard terminal deliveries when no typed terminal exchange is configured. Default false; incompatible with required dispatch. |
| `ShutdownTimeout` | Bound individual shutdown/transport cleanup waits, not one combined deadline covering every lock, callback, and resource. |

The three `Require...` options default to false. Do not infer required processing from the provider name or `IsDurable` alone.

## Required classic-subscription example

Provision a durable source queue and a durable quarantine destination before accepting required traffic. The quarantine exchange must route `processor` to the intended durable queue. Its TTL and overflow policies must preserve the required retention. The provider does not create the quarantine topology for you.

```csharp
using System.Collections.Generic;
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

`connectionString` is the application's configured URI; use `amqps` for encrypted transport. Register the actual handler and establish the intended subscriptions before publishing. The example configures a bus; constructing it does not by itself create every required destination.

Explicitly configure the actual queue type. The provider selects retry behavior from `Arguments["x-queue-type"]`; an omitted argument does not establish the virtual host's default queue type. Stable names identify logical subscriptions, not physical replicas. Do not redeclare existing queues with incompatible arguments or attempt an in-place classic-to-quorum conversion.

## Quorum broker and application budgets

`UseQuorumQueues()` selects nonexclusive, non-autodelete quorum topology and supplies `x-delivery-limit`. Keep `IsDurable = true`; the helper does not override an explicitly disabled durability setting. Direct options supply `DeliveryLimit` as the broker argument when that raw argument is absent. An explicitly supplied raw broker limit is preserved.

Application terminal handling and broker delivery-limit enforcement are distinct. The application reads the broker's `x-delivery-count` on quorum deliveries; connection-loss redeliveries can therefore affect its observed budget too. These are not two isolated counters. For required work, deliberately choose a policy:

- A finite broker budget with quorum **at-least-once broker dead-lettering**, `RejectPublish` overflow, an existing DLX, and a durable destination. The corresponding typed options are `DeadLetterStrategy.AtLeastOnce` and `Overflow = QueueOverflowBehavior.RejectPublish`. Check all broker prerequisites, including the required feature flag, and effective operator policy.
- An explicitly disabled broker budget (`Arguments["x-delivery-limit"] = -1L`) with a finite application `DeliveryLimit`. Monitor this choice: it removes the broker's own redelivery limit.

Do not silently disable production policy. Queue declaration changes can require migration. Administrative deletion, destructive TTL/overflow settings, unsafe broker dead-lettering, and permanent storage loss can destroy work without a client ACK.

## Retry and terminal handoffs

Classic retries go through the default exchange to the **actual failed subscription queue**, not back through the original fanout exchange. The logical message ID and root identity are retained and the retry count increases. The initial scheduling header is removed so that delay is not reapplied.

Retry and terminal publications use a dedicated **confirmed, mandatory** channel even when ordinary publisher confirms are disabled. The original is acknowledged only after successful handoff. Missing exchanges, unroutable returns, negative confirmations, and ambiguous outcomes retain the original and retry the transfer with bounded backoff, without rerunning its application handler during that handoff loop.

Replacement publication and original acknowledgement are not atomic. Lost confirmation or interruption after confirmation but before ACK can produce multiple copies with the same logical ID. Deduplicate by logical consumer and event ID; a global deduplication record must not suppress an independent subscription. Do not treat a non-atomic check-then-record operation as proof of idempotent business side effects.

Quorum retries below the application budget use broker-managed rejection/redelivery. Application exhaustion uses the terminal policy; broker-budget events need their own safe DLX policy.

| Terminal situation | Outcome |
|---|---|
| Typed `DeadLetterExchange` configured | Confirmed, mandatory terminal handoff, then ACK the source. Provision the destination separately. |
| Destination missing, unbound, or rejecting publication | Retain unacknowledged and retry with backoff. Readiness reports blocked state. Repairing the route lets that handoff resume. |
| No typed terminal destination, discard disabled | Retain unacknowledged and report blockage. Repair options/topology and deliberately replace the bus or perform authorized replay; do not treat permanent pending state as successful processing. |
| No destination, explicit discard enabled | Log the intentional discard and ACK. Not a required-event mode. |
| Malformed or overflowing retry metadata | Apply terminal handling rather than resetting the retry budget. |

Terminal copies preserve body and identity, remove expiration/scheduling delay, and add failure-type/original-routing metadata rather than exception text. Destination retention policies still apply. A raw or policy-only broker DLX does not select the client's terminal route: set typed `DeadLetterExchange` and `DeadLetterRoutingKey` for that handoff. Changing those declarations on an existing queue still requires a compatibility check.

## Dispatch and lifecycle

Required dispatch snapshots matching live handlers, validates the typed body before invocation, and awaits their completion. A raw `IMessage` handler deliberately accepts the envelope and is responsible for its own schema/payload validation; do not use a raw catch-all to imply typed validation occurred.

A removed local subscription does not block other matching live handlers. When no local subscribers remain, maintenance closes the consumer and unacknowledged work can return to a retained queue. An unmatched type while unrelated handlers remain is a strict-dispatch terminal failure. Ephemeral queue deletion and unsafe broker policies can still destroy that work.

Multiple matching handlers in one bus share a broker delivery. A failed delivery can repeat a local handler that already succeeded; use idempotency or separate durable queues for independent retry boundaries. Return a task covering actual side effects. Async-void and internal fire-and-forget work cannot be protected by awaiting the handler task.

A one-second maintenance loop repairs recoverable channel closure and consumer cancellation separately from client network recovery. Failed initialization rolls back its local registration and partial transport. Permanent declaration/permission faults remain explicit until corrected and subscription is retried.

Delivery-generation invalidation prevents late completion from settling a replacement generation. Raw payloads are owned copies because a handler may outlive its transport callback. Shutdown signals cancellation and bounds individual waits, but cannot terminate arbitrary application code; apply the host's overall shutdown deadline separately.

`IsSubscriptionReady` reflects local registration, transport/consumer state, and retained-delivery blockage, not a proof of business progress. `LastSubscriptionError` reports setup/recovery errors; `LastDeliveryError` reports retained-delivery/handoff errors, not queue depth. Monitor ready/unacknowledged counts, quarantine growth, alarms, and actual completion. Do not endlessly restart a blocked consumer: redelivery can exhaust a broker budget.

## Delayed publication

The provider's 4.2.5 test environment includes the independently versioned delayed-exchange plugin `4.2.0`. A specific unknown-exchange-type response or an existing regular fanout topic establishes that the topic cannot schedule. Other permission, declaration, and network failures propagate rather than silently selecting memory scheduling.

With `RequireBrokerDelayedDelivery`, unavailable broker scheduling rejects publication before creating a memory timer. Without it, the legacy in-process fallback logs a warning and remains best effort; pending work can be lost when the process stops.

The plugin stores scheduled messages on one broker node and routes them later. Confirmation is not proof of future routing or replicated availability. Provision durable future queues/bindings. Use an application outbox or independently durable scheduler when broker storage loss, plugin removal, or routing changes must be covered. `RequirePublishRouting` is an immediate-publication contract and rejects nonzero delay; do not combine these modes expecting future-route confirmation.

The provider's native delayed-retry and newer quorum consumer-timeout options require a later broker and are rejected on 4.2.5. No later-version feature is enabled by these examples.

## Compatibility and rollout

Release notes must identify retention replacing discard on exhaustion, subscription-local retries/stable IDs, strict endpoint identity/port checks, and the opt-in contracts. Retention can intentionally pause progress pending repair; do not hide that state by discarding required events.

Before adoption, inventory queue types/policies, provision terminal routes, validate certificate aliases, choose the required profile, and rehearse repair/replay with idempotent handlers. Provider tests do not make database commits and publication transactional. Application outbox/inbox/reconciliation, capacity, and deployment validation remain application responsibilities. The 4.2.5 pin is not a vulnerability or support-lifecycle sign-off.

## References

- [Provider configuration and TLS](./rabbitmq.md)
- [Verification and evidence boundaries](./rabbitmq-verification.md)
- [RabbitMQ 4.2 queues](https://www.rabbitmq.com/docs/4.2/queues)
- [RabbitMQ 4.2 confirmations and acknowledgements](https://www.rabbitmq.com/docs/4.2/confirms)
- [RabbitMQ 4.2 quorum dead-lettering](https://www.rabbitmq.com/docs/4.2/quorum-queues#dead-lettering)
- [Delayed-exchange plugin limitations](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/blob/v4.2.0/README.md)
