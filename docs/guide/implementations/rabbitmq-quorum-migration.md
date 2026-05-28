# Quorum Queue Migration

This guide covers migrating from classic queues to [quorum queues](https://www.rabbitmq.com/docs/quorum-queues) when using `Foundatio.RabbitMQ`.

## Why Migrate?

Quorum queues provide:

- **Replication** across cluster nodes via Raft consensus
- **Automatic failover** when a node goes down (majority must survive)
- **Poison message protection** with built-in delivery limits
- **No data loss** during rolling upgrades (with majority available)
- **Native delayed retries** (4.3+): Linear backoff without the delayed message exchange plugin

Classic queues are single-node: if that node goes down, the queue is unavailable until it recovers.

## Enabling Quorum Queues

```csharp
var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString("amqp://guest:guest@localhost:5672")
    .Topic("my-events")
    .UseQuorumQueues()       // sets x-queue-type=quorum
    .DeliveryLimit(5));      // max redelivery attempts before dead-lettering
```

`UseQuorumQueues()` automatically:

- Sets `x-queue-type = "quorum"` in queue arguments
- Disables `autoDelete` and `exclusive` (incompatible with quorum queues)
- Uses `reject` (not republish) for messages exceeding the delivery limit

## Migration Challenge

You **cannot** change an existing classic queue to quorum in-place. RabbitMQ returns `PRECONDITION_FAILED` (406) when declaring an existing queue with a different `x-queue-type`.

## Migration Approaches

### 1. Delete and Recreate (Simplest)

Best for queues that can tolerate brief downtime and message loss.

```bash
# 1. Stop all consumers
# 2. Drain or discard remaining messages
rabbitmqctl delete_queue my-queue
# 3. Redeploy with UseQuorumQueues()
# 4. Start consumers
```

### 2. New Queue Name

Best when you can coordinate a deployment that changes the queue name.

```csharp
// Before
o.Topic("process-events");

// After - new name, quorum type
o.Topic("process-events-v2")
 .UseQuorumQueues();
```

Use the [Shovel plugin](https://www.rabbitmq.com/docs/shovel) to drain remaining messages from the old queue into the new one.

### 3. Server-Side Default Queue Type

Set the default queue type at the vhost level so all new queues are quorum without code changes:

```bash
rabbitmqctl set_policy quorum-default ".*" \
  '{"x-queue-type": "quorum"}' \
  --apply-to queues
```

::: warning
This only affects **new** queues. Existing classic queues are unchanged.
:::

### 4. Relaxed Property Equivalence

RabbitMQ 4.x supports suppressing type-mismatch errors during migration:

```ini
# rabbitmq.conf
quorum_queue.property_equivalence.relaxed_checks_on_redeclaration = true
```

This allows declaring an existing classic queue with `x-queue-type=quorum` without error - but it does **not** convert the queue. It only suppresses the error to allow gradual rollout.

### 5. Blue-Green Deployment

For zero-downtime migration of critical queues:

1. Create a new vhost with `default_queue_type = quorum`
2. Set up [Federation](https://www.rabbitmq.com/docs/federation) from old vhost to new
3. Deploy consumers against the new vhost
4. Deploy publishers against the new vhost
5. Decommission old vhost after draining

## Incompatible Features

These classic queue features are **not available** on quorum queues:

| Feature | Alternative |
|---------|-------------|
| `exclusive = true` | Not supported; use unique queue names + TTL |
| `autoDelete = true` | Not supported; use `x-expires` for idle cleanup |
| `x-queue-mode: lazy` | Quorum queues are always lazy (memory-optimized) by default |
| `x-max-priority` (classic) | Supported on RabbitMQ 4.3+ with 32 strict priority levels |
| Global QoS | Use per-consumer QoS only |

## Recommended Configuration

```csharp
var messageBus = new RabbitMQMessageBus(o => o
    .ConnectionString("amqp://guest:guest@node1:5672,node2:5672,node3:5672")
    .Topic("my-events")
    .UseQuorumQueues()
    .DeliveryLimit(5)
    .PrefetchCount(20)
    .PublisherConfirmsEnabled(true)
    .RequestedHeartbeat(TimeSpan.FromSeconds(30))
    .DeadLetterExchange("dlx"));
```

Key points:

- **Multiple hosts**: Always provide all cluster nodes for failover
- **PrefetchCount**: Use 10-50 for quorum queues (higher than classic due to Raft consensus latency)
- **Publisher confirms**: Essential for guaranteed delivery with quorum queues
- **Heartbeat**: Tune for your network (too low = false positives, too high = slow detection)
- **Dead letter exchange**: Route poison messages instead of dropping them

## Consumer Timeout

Quorum queues on RabbitMQ 4.3+ evaluate consumer timeouts (default 30 min). If your handlers are slow, increase the timeout via broker config:

```ini
# rabbitmq.conf
consumer_timeout = 3600000
```

## Verification

After migration, verify quorum queue status:

```bash
# Check queue type and replicas
rabbitmqctl list_queues name type members online

# Verify quorum is healthy
rabbitmqctl list_queues name type leader members
```

## Next Steps

- [RabbitMQ Implementation](/guide/implementations/rabbitmq) - Full configuration reference
- [Messaging Guide](/guide/messaging) - Pub/sub patterns and best practices
- [RabbitMQ Quorum Queue Documentation](https://www.rabbitmq.com/docs/quorum-queues) - Official reference
