# Foundatio.Messaging.Benchmarks

Measures v2 messaging (`IMessageClient` and handlers), the legacy Foundatio API (`IQueue<T>`, `IMessageBus`) and the raw
client SDK doing the same work, on the same broker. Each run is one process and prints one JSON line (throughput, client
CPU and allocations per message, GC counts, latency percentiles, broker calls); `--out file.jsonl` appends it.

```bash
dotnet build benchmarks/Foundatio.Messaging.Benchmarks -c Release
B=benchmarks/Foundatio.Messaging.Benchmarks/bin/Release/net10.0/Foundatio.Messaging.Benchmarks

# drain a prefilled backlog of 20,000 100-byte messages with 16 handlers
taskset -c 6-9 $B --transport redis --api v2 --scenario consume --concurrency 16 --payload 100 --count 20000
```

| Option | Values |
| --- | --- |
| `--transport` | `inmemory` (v2 only: core's own cost), `redis`, `rabbitmq`, `kafka` (`--kip848` for the consumer group protocol), `sqs` (`--fifo`), `servicebus` |
| `--api` | `v2`, `legacy`, `raw` |
| `--pattern` | `queue` (send to a queue) or `topic` (publish to a durable subscription) |
| `--scenario` | `publish` (single sends from `--publishers` tasks), `publishbatch` (`--batch` per call), `consume` (drain a prefilled backlog of `--count`), `latency` (send at `--rate` per second for `--duration` seconds; p50/p95/p99 from send to handler), `idle` (receivers running on an empty source for `--duration` seconds), `startup` (start a receiver, then time the first message) |
| `--concurrency` | handler concurrency (1, 16, 64) |
| `--payload` | body size in bytes |
| `--groups`, `--delay-ms`, `--work-ms` | ordered groups, delayed delivery, handler time |
| `--alloc-types` | print the types allocated during the measurement |

Broker calls are counted from the client: Redis commands through the StackExchange.Redis profiler, SQS requests by
`X-Amz-Target`, Service Bus operations from the SDK's EventSource, and RabbitMQ message counts from the management API
(`RABBITMQ_MANAGEMENT=http://guest:guest@host:port`). Kafka calls are not counted. Connection strings come from
`REDIS_CONNECTION`, `RABBITMQ_CONNECTION`, `KAFKA_BOOTSTRAP_SERVERS`, `SQS_CONNECTION` and `SERVICEBUS_CONNECTION`.

The raw targets are careful hand-written consumers (batched receives, group-committed acknowledgements where the broker
has them) without leases, retries, dead letters or delays, so they are a floor, not a like-for-like replacement. Pin
the process with `taskset`, run one broker at a time, and interleave the configurations you compare.
