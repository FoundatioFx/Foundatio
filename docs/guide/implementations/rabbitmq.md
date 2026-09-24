---
title: RabbitMQ
---

# Foundatio.RabbitMQ

`RabbitMQMessageBus` implements `IMessageBus` using RabbitMQ and AMQP 0.9.1. It supports best-effort pub/sub and explicitly configured durable subscriptions; installing the provider alone does not establish reliable business processing.

::: warning Companion implementation
This documentation branch accompanies a provider review stack, in landing order: [#103: TLS endpoints](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/103) → [#106: quorum priority](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/106) → [#104: broker verification](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/104) → [#105: delivery and recovery](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/105) → [#100: sample and documentation](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100). PR #105 owns delivery/recovery behavior and breaking exhaustion change. These contracts are not claimed to be in an already released NuGet package. Coordinate documentation publication with the stack's merge/release and verify the package version used by your application. Aggregate implementation reference: [`7d01c8b`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/tree/7d01c8beec9fc798d36fe1d03d3e78976375f34b). Revision-specific validation remains in the companion PRs.
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

The [delivery-safety guide](./rabbitmq-delivery-safety.md) covers the new opt-in `RequireSuccessfulDispatch`, `RequirePublishRouting`, and `RequireBrokerDelayedDelivery` contracts, default retention on exhaustion, terminal topology, and shutdown behavior. These provider processing and confirmed-handoff contracts support both classic and quorum queues. Quorum adds replication and optional at-least-once **broker** dead-lettering; migrating queue type is optional. See the [candidate options reference](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/7d01c8beec9fc798d36fe1d03d3e78976375f34b/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBusOptions.cs) for the complete API. For custom topology and metadata code, [`Foundatio.Utility.RabbitMQConstants`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/7d01c8beec9fc798d36fe1d03d3e78976375f34b/src/Foundatio.RabbitMQ/Utility/RabbitMQConstants.cs) exposes shared header and queue-argument wire names.

`IMessage.Properties` formats received numeric AMQP headers with `CultureInfo.InvariantCulture`: a numeric value of `1.5` becomes `"1.5"` even under `fr-FR`, rather than `"1,5"`. Byte-array headers still decode as UTF-8. Use invariant culture when parsing numeric property strings.

## TLS and endpoints

`amqps` enables TLS for URI-only and replacement-host connections. Each actual hostname or IP address must be covered by the server certificate and its issuing chain must be trusted. A certificate covering only the URI hostname is insufficient when replacement hosts use other names. Server certificate validation remains strict for every endpoint.

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

For separate RabbitMQ.Client setup connections, the public [`Foundatio.Utility.RabbitMQEndpointResolver.CreateEndpoints(ConnectionFactory, IList<string>? hosts = null)`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/7d01c8beec9fc798d36fe1d03d3e78976375f34b/src/Foundatio.RabbitMQ/Utility/RabbitMQEndpointResolver.cs) applies the same endpoint rules using the factory's URI. The subscriber sample calls this API from the compiled provider library when provisioning quarantine.

The resolver preserves client-certificate settings already configured on `ConnectionFactory.Ssl`: `CertPath`, `CertPassphrase`, `Certs`, `CertificateSelectionCallback`, and `ClientCertificateContext`, along with protocol and revocation settings. It sets each endpoint's server name to the actual host and forces `AcceptablePolicyErrors` to `None`. A non-null `CertificateValidationCallback` is rejected with `ArgumentException`, even if that callback intends to validate strictly. These settings apply to connections you create with the supplied factory; `RabbitMQMessageBusOptions` does not expose a separate client-certificate configuration option.

## Broker baseline and priority

The companion provider branch pins all repository-managed brokers to **RabbitMQ 4.2.5**: Compose, the Aspire primary/chaos brokers, the delayed-plugin image base, and both TLS test brokers. The delayed-exchange plugin artifact is independently versioned `4.2.0`. This is a compatibility baseline, not a broker security/support-lifecycle certification or a change to deployed infrastructure. No 4.3 upgrade is part of this work.

| Feature | RabbitMQ 4.2.x (tested on 4.2.5) | RabbitMQ 4.3+ |
|---|---|---|
| Classic priorities | `UseMessagePriority(maxPriority)` sets `x-max-priority` | Same |
| Quorum priorities | Built-in normal/high tiers | 32 built-in strict levels, 0–31 |
| Native quorum retries | `UseDelayedRetries()` rejected | Linear backoff for returned deliveries |
| Provider quorum timeout option | `ConsumerTimeout()` rejected | Sets `x-consumer-timeout` for quorum queues |
| Initial scheduling via delayed-exchange plugin | Available with a compatible plugin and topic | Plugin unavailable; this provider falls back to in-process scheduling unless required broker scheduling rejects it |
| Single active consumer | Supported | Supported |

The [RabbitMQ 4.3 release notes](https://www.rabbitmq.com/blog/2026/04/23/rabbitmq-4.3-release) describe the newer quorum capabilities. This matrix preserves the provider's version-gated support; the repository's 4.2.5 suite does not establish 4.3+ runtime verification. The `ConsumerTimeout()` row describes this provider's quorum option, not the broker's older acknowledgement-timeout mechanisms.

`UseMessagePriority(maxPriority)` does not enable or cap quorum priorities on either version. Set message priority through `MessageOptions.Properties["Priority"]`. RabbitMQ.Client 7.2.2 omits a zero-valued priority, so the broker's omitted-priority behavior applies. Priority does not recall deliveries already sent to consumers. On 4.3, returned quorum deliveries use return order rather than their original priority; see [RabbitMQ priority semantics](https://www.rabbitmq.com/docs/priority).

Prefetch limits unacknowledged deliveries, not business-handler concurrency, and does not bound `FireAndForget` consumers. Single active consumer does not eliminate redeliveries or guarantee business side-effect ordering.

## Delayed delivery

`MessageOptions.DeliveryDelay` uses the delayed-exchange plugin when the topic supports it on a broker before 4.3. The [archived plugin](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange) depends on Mnesia, removed in RabbitMQ 4.3, so the provider skips its probe on a detected 4.3+ broker. Without a scheduling-capable topic, the default fallback holds work in process memory; pending messages are lost if the publisher stops. `RequireBrokerDelayedDelivery`, with durable publication and confirms, rejects that fallback before creating a timer.

Native quorum delayed retries on 4.3+ apply to deliveries returned to the queue. `UseDelayedRetries()` does not schedule an initial `MessageOptions.DeliveryDelay` publication. Use an application outbox or independently durable scheduler when required initial scheduling cannot use the plugin.

The plugin stores scheduled work on one broker node and routes it later. Publisher-process independence does not imply replicated scheduling or guaranteed future routing. Immediate routing validation and scheduled publication are separate contracts: `RequirePublishRouting` rejects nonzero delays. See [delayed-publication requirements](./rabbitmq-delivery-safety.md#delayed-publication), including the need to provision future queues/bindings and the cases that require an outbox or another scheduler.

## Tracing

Foundatio's message handler spans use the `Foundatio` activity source. Add `RabbitMQ.Client.*` to collect the client's transport spans as well:

```csharp
services.AddOpenTelemetry().WithTracing(tracing =>
{
    tracing.AddSource("Foundatio");
    tracing.AddSource("RabbitMQ.Client.*");
    tracing.AddOtlpExporter();
});
```

When `MessageOptions.CorrelationId` is absent or empty, `MessageBusBase` copies `Activity.Current.Id` into it and copies a nonempty `Activity.Current.TraceStateString` into the `TraceState` message property. RabbitMQ sends the correlation ID in the AMQP `CorrelationId` basic property and `TraceState` in a header. Supplying a correlation ID skips both automatic copies.

On receipt, Foundatio creates a handler activity parented to the received correlation ID and restores `TraceState`. `MessageBusBase` does not create a publish activity. Verify how any additional propagation mechanism interacts with this mapping before enabling it.

## Further guidance

- [Delivery safety and adoption](./rabbitmq-delivery-safety.md)
- [Provider verification and test conventions](./rabbitmq-verification.md)
- [Optional classic-to-quorum migration](./rabbitmq-quorum-migration.md)
- [Messaging](../messaging.md)
- [Serialization](../serialization.md)

These guides are maintained in **FoundatioFx/Foundatio**. The provider repository keeps source, XML API comments, tests, and a README linking here.
