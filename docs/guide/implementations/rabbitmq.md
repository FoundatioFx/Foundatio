---
title: RabbitMQ
---

# Foundatio.RabbitMQ

`RabbitMQMessageBus` implements `IMessageBus` using RabbitMQ and AMQP 0.9.1. It supports best-effort pub/sub and explicitly configured durable subscriptions; installing the provider alone does not establish reliable business processing.

## Overview

| Implementation | Interface | Package |
|----------------|-----------|---------|
| `RabbitMQMessageBus` | `IMessageBus` | Foundatio.RabbitMQ |

::: warning Companion implementation
The provider's [#103: TLS endpoints](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/103) has merged into `main`. This documentation branch accompanies the remaining provider review stack: [#106: reject classic-only priority limits on quorum](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/106) → [#104: broker verification](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/104) → [#105: delivery and recovery](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/105) → [#100: sample and documentation](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100). PR #106 owns the breaking quorum-priority configuration validation; PR #105 owns delivery/recovery behavior, including the classic exhaustion correction and strict opt-in terminal transfer. These contracts are not claimed to be in an already released NuGet package. Coordinate documentation publication with the stack's merge/release and verify the package version used by your application. Aggregate implementation reference: [`9e20a4c`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/tree/9e20a4c6c115e75837cd028dd4dbc626635d39e6). Revision-specific validation remains in the companion PRs.
:::

::: warning Breaking changes
Quorum configurations must remove `UseMessagePriority()` and leave `MaxPriority` unset. Combining either setting with quorum now throws `InvalidOperationException`; existing callers using that combination must change before adoption. Message-level `Priority` remains supported for both queue types.

Strict TLS identity and endpoint/port validation can reject previously accepted configurations. Check the [endpoint rules](#tls-and-endpoints).
:::

Ordinary Automatic dispatch (`RequireSuccessfulDispatch = false`) rejects exhausted deliveries without requeue for both classic and quorum queues. The broker applies DLX arguments or policy, or discards when no DLX exists; an empty typed option does not imply missing broker configuration. Classic exhaustion previously ACKed even with a configured DLX; rejection corrects that behavior. Strict dispatch remains an explicit opt-in requiring Automatic acknowledgements and a nonempty typed `DeadLetterExchange`; its failed application transfers retain the source and retry. No retention migration is required for ordinary Automatic users. `FireAndForget` remains the default acknowledgement mode.

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

The [delivery-safety guide](./rabbitmq-delivery-safety.md) covers the opt-in `RequireSuccessfulDispatch`, `RequirePublishRouting`, and `RequireBrokerDelayedDelivery` contracts, ordinary broker dead-letter handling, strict application terminal transfer, and shutdown behavior. These provider processing and confirmed-handoff contracts support both classic and quorum queues. Broker dead-lettering is at-most-once by default; quorum adds replication and optional at-least-once **broker** dead-lettering. Migrating queue type is optional. See the [candidate options reference](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/9e20a4c6c115e75837cd028dd4dbc626635d39e6/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBusOptions.cs) for the complete API. For custom topology and metadata code, [`Foundatio.Utility.RabbitMQConstants`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/9e20a4c6c115e75837cd028dd4dbc626635d39e6/src/Foundatio.RabbitMQ/Utility/RabbitMQConstants.cs) exposes shared header and queue-argument wire names.

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

The resolver throws `ArgumentOutOfRangeException` for a parsed port outside 1–65535, such as `broker:0` or `broker:65536`. Malformed host entries and unparseable port text throw `ArgumentException`.

Strict identity checking and validation of previously ignored malformed ports can break an existing configuration. Correct the aliases/certificates/ports rather than disabling verification or downgrading to plaintext.

For separate RabbitMQ.Client setup connections, the public [`Foundatio.Utility.RabbitMQEndpointResolver.CreateEndpoints(ConnectionFactory, IList<string>? hosts = null)`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/9e20a4c6c115e75837cd028dd4dbc626635d39e6/src/Foundatio.RabbitMQ/Utility/RabbitMQEndpointResolver.cs) applies the same endpoint rules using the factory's URI. The subscriber sample calls this API from the compiled provider library when provisioning quarantine.

The resolver preserves client-certificate settings already configured on `ConnectionFactory.Ssl`: `CertPath`, `CertPassphrase`, `Certs`, `CertificateSelectionCallback`, and `ClientCertificateContext`, along with protocol and revocation settings. It sets each endpoint's server name to the actual host and forces `AcceptablePolicyErrors` to `None`. A non-null `CertificateValidationCallback` is rejected with `ArgumentException`, even if that callback intends to validate strictly. These settings apply to connections you create with the supplied factory; `RabbitMQMessageBusOptions` does not expose a separate client-certificate configuration option.

## Broker baseline and priority

The companion provider branch keeps Compose, the Aspire primary/chaos brokers, the delayed-plugin image base, and both TLS test brokers on **RabbitMQ 4.2.5**. A separate owned **4.3.6** broker runs priority compatibility scenarios in CI. The delayed-exchange plugin artifact remains `4.2.0`. These test pins do not change deployed infrastructure or certify a broker's security/support lifecycle.

| Feature | RabbitMQ 4.2.x (tested on 4.2.5) | RabbitMQ 4.3+ |
|---|---|---|
| Classic priorities | `UseMessagePriority(maxPriority)` sets `x-max-priority` | Same |
| Quorum priorities | Built-in normal/high tiers | 32 built-in strict levels, 0–31 |
| Native quorum retries | `UseDelayedRetries()` rejected | Linear backoff for returned deliveries |
| Provider quorum timeout option | `ConsumerTimeout()` rejected | Sets `x-consumer-timeout` for quorum queues |
| Initial scheduling via delayed-exchange plugin | Available with a compatible plugin and topic | Plugin unavailable; this provider falls back to in-process scheduling unless required broker scheduling rejects it |
| Single active consumer | Supported | Supported |

The [RabbitMQ 4.3 release notes](https://www.rabbitmq.com/blog/2026/04/23/rabbitmq-4.3-release) describe the newer quorum capabilities. The 4.3.6 broker runs the priority scenarios and bounded-source classic retry cases; it does not verify native delayed retries, consumer timeouts, or full-provider compatibility. The `ConsumerTimeout()` row describes this provider's quorum option, not the broker's older acknowledgement-timeout mechanisms.

Classic priority ranges are unchanged: `UseMessagePriority(maxPriority)` accepts 1–32; direct `MaxPriority` accepts 1–255 and rejects zero with `ArgumentOutOfRangeException`. Higher limits cost more broker CPU and memory.

Combining `UseMessagePriority()` with `UseQuorumQueues()` throws `InvalidOperationException` immediately in either builder order. Direct options allow either assignment order and reject the combination at bus construction. Quorum priorities are built in; leave `MaxPriority` unset.

Prefer fluent builders for supported settings such as `UseQuorumQueues()` and `UseMessagePriority()`. Use `Arguments` for other queue settings; the [options reference](https://github.com/FoundatioFx/Foundatio.RabbitMQ/blob/9e20a4c6c115e75837cd028dd4dbc626635d39e6/src/Foundatio.RabbitMQ/Messaging/RabbitMQMessageBusOptions.cs) includes a queue message-TTL example and links to broker documentation. Configure arguments before constructing the bus. Before queue declaration, the provider rechecks for quorum/`MaxPriority` conflicts and changes to or from explicit quorum since construction. Mutating running-bus arguments is unsupported.

Quorum detection matches only the local `Arguments["x-queue-type"]` string against `"quorum"`, case-insensitively. It does not infer broker/vhost defaults, inspect existing queues, or validate other queue-type values. Explicitly configure the intended type and verify the deployed topology.

Set message priority through `MessageOptions.Properties["Priority"]` for either classic or quorum queues. RabbitMQ.Client 7.2.2 omits a zero-valued priority, so the broker's omitted-priority behavior applies. Priority does not recall deliveries already sent to consumers. On 4.3, returned quorum deliveries use return order rather than their original priority; see [RabbitMQ priority semantics](https://www.rabbitmq.com/docs/priority).

Prefetch limits unacknowledged deliveries, not business-handler concurrency, and does not bound `FireAndForget` consumers. Single active consumer does not eliminate redeliveries or guarantee business side-effect ordering.

### Priority behavior across broker upgrades

Audit publishers that mix explicit, omitted, and zero priorities before upgrading quorum queues. The same publications can arrive in a different order without application changes:

| Quorum behavior | RabbitMQ 3.13.7 | RabbitMQ 4.2.5 | RabbitMQ 4.3.6 |
|---|---|---|---|
| Queued priorities 5 and 10 | FIFO; priority ignored | Same high group; FIFO within it | 10 before 5 |
| Omitted/zero versus explicit 1 with RabbitMQ.Client 7.2.2 | FIFO | Same normal group; FIFO within it | Omitted/zero uses priority 4, ahead of 1 |
| Progress for lower-priority traffic | FIFO | Normal traffic receives a share | Sustained higher-priority traffic can delay it indefinitely |

RabbitMQ.Client 7.2.2 omits a zero-valued priority on the wire. On 4.3 quorum queues, both zero and omission therefore use the broker default of 4. See the [3.13 queue comparison](https://www.rabbitmq.com/docs/3.13/quorum-queues), [4.2 priority rules](https://www.rabbitmq.com/docs/4.2/priority), and [4.3 priority rules](https://www.rabbitmq.com/docs/priority).

The focused scenarios also check classic numeric ordering on all three versions, that priority cannot preempt a delivery already in flight, and that confirmed publications without a bound subscription queue are not retained for a later subscriber. Establish the required queues and bindings before publishing; confirms alone do not establish routing. These priority checks do not constitute a full compatibility matrix for those broker versions. See the [verification scope](./rabbitmq-verification.md#priority-upgrade-coverage).

## Delayed message delivery

`MessageOptions.DeliveryDelay` uses the delayed-exchange plugin when the topic supports it on a broker before 4.3. The [archived plugin](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange) depends on Mnesia, removed in RabbitMQ 4.3, so the provider skips its probe on a detected 4.3+ broker. Without a scheduling-capable topic, the default fallback holds work in process memory; pending messages are lost if the publisher stops. `RequireBrokerDelayedDelivery`, with durable publication and confirms, rejects that fallback before creating a timer.

Topic setup logs a warning when it detects the archived plugin or skips its probe on an incompatible broker.

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

## Next Steps

- [Delivery safety and adoption](./rabbitmq-delivery-safety.md)
- [Provider verification and test conventions](./rabbitmq-verification.md)
- [Optional classic-to-quorum migration](./rabbitmq-quorum-migration.md)
- [Messaging](../messaging.md)
- [Serialization](../serialization.md)

These guides are maintained in **FoundatioFx/Foundatio**. The provider repository keeps source, XML API comments, tests, and a README linking here.
