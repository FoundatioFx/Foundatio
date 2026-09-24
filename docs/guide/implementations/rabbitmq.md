---
title: RabbitMQ
---

# Foundatio.RabbitMQ

`RabbitMQMessageBus` implements `IMessageBus` using RabbitMQ and AMQP 0.9.1. It supports best-effort pub/sub and explicitly configured durable subscriptions; installing the provider alone does not establish reliable business processing.

::: warning Companion implementation
This documentation branch accompanies [Foundatio.RabbitMQ PR #100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100). New strict-delivery options and changed exhaustion behavior described here are not claimed to be in an already released NuGet package. Coordinate documentation publication with that implementation's merge/release and verify the package version used by your application. Implementation reference: [`d786795`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/tree/d7867954a00e27a856bcd7ef79b026bfd7800218). Revision-specific validation remains in the companion PRs.
:::

::: warning Breaking exhaustion behavior
With `AcknowledgementStrategy.Automatic`, an exhausted delivery without a typed terminal destination is now retained by default instead of discarded. Retention can stop consumer progress and grow the backlog. Before adoption, provision quarantine, finite prefetch, capacity limits, and an operator repair procedure. `FireAndForget` remains the default. Strict dispatch remains opt-in and requires Automatic acknowledgements, a nonempty typed `DeadLetterExchange`, and discard disabled.
:::

## Installation

```bash
dotnet add package Foundatio.RabbitMQ
```

## Usage

The callback receives a fluent options **builder**, not an options object. Alternatively, pass a `RabbitMQMessageBusOptions` instance to the constructor.

```csharp
using Foundatio.Messaging;

await using var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString("amqp://guest:guest@localhost:5672")
    .Topic("events"));

await messageBus.SubscribeAsync<OrderCreated>(order =>
{
    Console.WriteLine($"Order created: {order.OrderId}");
});

await messageBus.PublishAsync(new OrderCreated { OrderId = 123 });
```

`OrderCreated` is your application message type. This local plaintext example uses the best-effort defaults and is not a required-delivery configuration. A successful publish is not confirmation that the subscriber has finished; keep the application/bus alive for the intended subscription lifetime. Use `amqps` for encrypted transport.

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `ConnectionString` | Required | URI supplying credentials, virtual host, scheme, and the default endpoint. |
| `Hosts` | `null` | A nonempty list replaces the URI host; endpoint selection is randomized. |
| `IsDurable` | `true` | Durable exchange/queue declarations and persistent publications; not an end-to-end processing guarantee. |
| `SubscriptionQueueName` | Empty | Use a stable, nonempty logical subscription name for retained work. |
| `IsSubscriptionQueueExclusive` | `true` | The queue belongs to its connection. Set false for retained/shared subscriptions. |
| `SubscriptionQueueAutoDelete` | `true` | Delete the queue after its last consumer is removed. Set false for retained subscriptions. |
| `AcknowledgementStrategy` | `FireAndForget` | Broker automatic acknowledgement, not acknowledgement after handler success. |
| `PublisherConfirmsEnabled` | `false` | Wait for broker confirmation of ordinary publication when enabled. |
| `DeliveryLimit` | `2` | Application redelivery budget after the initial attempt; `-1` is unlimited. Broker enforcement is separate. |
| `PrefetchCount` | `0` | With both prefetch options zero, the provider sends no QoS and broker defaults can apply. |

The [delivery-safety guide](./rabbitmq-delivery-safety.md) covers the new opt-in `RequireSuccessfulDispatch`, `RequirePublishRouting`, and `RequireBrokerDelayedDelivery` contracts, default retention on exhaustion, terminal topology, and shutdown behavior. These provider processing and confirmed-handoff contracts support both classic and quorum queues. Quorum adds replication and optional at-least-once **broker** dead-lettering; migrating queue type is optional. See the [candidate options reference](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/d7867954a00e27a856bcd7ef79b026bfd7800218/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBusOptions.cs) for the complete API.

## TLS and endpoints

`amqps` enables TLS for URI-only and replacement-host connections. Each actual hostname or IP address must be covered by the server certificate and its issuing chain must be trusted. A certificate covering only the URI hostname is insufficient when replacement hosts use other names. The provider does not add a certificate-validation bypass or client-certificate authentication API.

A nonempty `Hosts` list replaces, rather than supplements, the URI endpoint. Credentials and virtual host still come from the URI. Do not put a comma-separated host list inside the URI; use `Hosts`:

```csharp
await using var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString(connectionString)
    .Hosts("broker-a.example:5671", "broker-b.example:5671")
    .Topic("events"));
```

Here `connectionString` must be an `amqps` URI for the intended credentials and virtual host. The configured endpoint names must match the deployed certificates.

For URI-only connections, an explicit URI port is preserved. A host-list entry without a port uses the scheme default: 5671 for `amqps`, 5672 for `amqp`, not a custom port from the URI. Duplicate host/port pairs are collapsed case-insensitively. Invalid ports and malformed entries are rejected. A null or empty list uses the URI endpoint; a nonempty list containing only unusable entries is rejected. Use `[IPv6]:port` for an IPv6 address with a port; bare IPv6 uses the scheme default. List order does not define primary/secondary preference.

Strict identity checking and validation of previously ignored malformed ports can break an existing configuration. Correct the aliases/certificates/ports rather than disabling verification or downgrading to plaintext.

## Broker baseline and priority

The companion provider branch pins all repository-managed brokers to **RabbitMQ 4.2.5**: Compose, the Aspire primary/chaos brokers, the delayed-plugin image base, and both TLS test brokers. The delayed-exchange plugin artifact is independently versioned `4.2.0`. This is a compatibility baseline, not a broker security/support-lifecycle certification or a change to deployed infrastructure. No 4.3 upgrade is part of this work.

On 4.2.5, quorum queues have normal/high priority tiers, not strict numeric priority levels. `UseMessagePriority(maxPriority)` configures `x-max-priority` for explicitly configured classic queues; it does not enable or cap quorum priorities. Set message priority through `MessageOptions.Properties["Priority"]`. RabbitMQ.Client 7.2.2 omits a zero-valued priority, so the broker's omitted-priority behavior applies. Priority does not recall deliveries already sent to consumers.

Prefetch limits unacknowledged deliveries, not business-handler concurrency, and does not bound `FireAndForget` consumers. Single active consumer does not eliminate redeliveries or guarantee business side-effect ordering. The provider retains guards for later-broker features such as native delayed retries and its newer quorum consumer-timeout option; those options are rejected on this 4.2.5 baseline.

## Delayed delivery

`MessageOptions.DeliveryDelay` uses the delayed-exchange plugin when the topic supports it. Without a scheduling-capable topic, the legacy fallback holds work in process memory. The companion implementation's `RequireBrokerDelayedDelivery` rejects that fallback before creating a timer.

The plugin stores scheduled work on one broker node and routes it later. Publisher-process independence does not imply replicated scheduling or guaranteed future routing. Immediate routing validation and scheduled publication are separate contracts: `RequirePublishRouting` rejects nonzero delays. See [delayed-publication requirements](./rabbitmq-delivery-safety.md#delayed-publication), including the need to provision future queues/bindings and the cases that require an outbox or another scheduler.

## Tracing

Foundatio's application-level message spans use the `Foundatio` activity source. Add `RabbitMQ.Client.*` to collect the client's transport spans as well:

```csharp
services.AddOpenTelemetry().WithTracing(tracing =>
{
    tracing.AddSource("Foundatio");
    tracing.AddSource("RabbitMQ.Client.*");
    tracing.AddOtlpExporter();
});
```

Foundatio propagates correlation/trace metadata through its message abstraction. Do not assume two independently configured propagation mechanisms compose correctly; verify their interaction before adding another.

## Further guidance

- [Delivery safety and adoption](./rabbitmq-delivery-safety.md)
- [Provider verification and test conventions](./rabbitmq-verification.md)
- [Optional classic-to-quorum migration](./rabbitmq-quorum-migration.md)
- [Messaging](../messaging.md)
- [Serialization](../serialization.md)

These guides are maintained in **FoundatioFx/Foundatio**. The provider repository keeps source, XML API comments, tests, and a short README linking here.
