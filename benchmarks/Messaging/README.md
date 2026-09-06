# Distributed messaging benchmarks

A sustained-load harness for the unreleased messaging API. It complements the existing BenchmarkDotNet microbenchmarks with acknowledged queue throughput, pub/sub fanout, end-to-end latency, allocations, CPU/GC, backlog and delivery validation.

See [measured results and findings](RESULTS.md) for the checked-in baseline, the timer-retention fix it exposed, and unresolved native crash evidence.

## Run locally

Requires .NET 10, PowerShell 7 and Docker. Start disposable, isolated brokers; the compose file limits each broker to four CPUs and enables Redis AOF with `appendfsync everysec`.

```powershell
docker compose -f benchmarks/Messaging/docker-compose.yml up -d
dotnet build benchmarks/Messaging.Tests -c Release
dotnet benchmarks/Messaging.Tests/bin/Release/net10.0/Foundatio.Messaging.Benchmarks.Tests.dll
./benchmarks/Messaging/run.ps1 -Profile smoke -NoBuild
./benchmarks/Messaging/run.ps1 -Profile standard -Repetitions 3 -Seconds 15 -NoBuild
./benchmarks/Messaging/run.ps1 -Profile extended -Repetitions 3 -Seconds 15 -NoBuild
./benchmarks/Messaging/run.ps1 -Profile soak -NoBuild

docker compose -f benchmarks/Messaging/docker-compose.yml down -v
```

Profiles:

- `smoke`: one second each of concurrent queues and four-way fanout; correctness only.
- `standard`: serial queues, concurrent queues, one-subscriber events and four-subscriber fanout. Payload is 1 KiB, with up to three seconds of warmup followed by fifteen seconds of publishing by default. Warmup is capped at one million inputs; the measured publishing window must complete in full.
- `extended`: 16 KiB queue/fanout payloads and ten-input queue/fanout batch API calls.
- `soak`: two-minute concurrent queue and four-subscriber fanout runs per implementation.

The standard profile runs 20 configurations × 3 repetitions = 60 fresh processes. Allow roughly 20–30 minutes, including warmup, broker setup, draining and cleanup. Trials run sequentially in seeded shuffled order, so two contenders never load the same broker simultaneously. Time windows exclude topology creation, startup, warmup, cleanup and JSON report generation. Do not build, run tests, profile, or run other workloads concurrently with measurements.

Results go to a timestamped `results/` directory: individual JSON/log files, throughput ranges and medians in `summary.md`, `summary.csv`, runtime information and repository state. The measurement executable exits nonzero for send/receive failures, missing or invalid deliveries, duplicates, timeout, cleanup failure or exhausted tracking capacity. Invalid trials are excluded from successful summaries and listed explicitly. Inspect failures before comparing throughput.

Each matrix invocation requires an empty output directory. This preserves earlier trials and prevents an old successful JSON file from being mistaken for the result of a new worker that crashed.

## What is compared

| Engine | Transport | Meaning |
| --- | --- | --- |
| Foundatio | In-memory | Full serialization, routing, receive and acknowledgement path |
| MassTransit 8.5.10 | In-memory | Full MassTransit pipeline using its own in-memory transport |
| Foundatio | Redis Streams | Real Redis broker, durable consumer groups and acknowledgements |
| Foundatio | SQS/SNS | Shared-broker comparison against MassTransit |
| MassTransit 8.5.10 | SQS/SNS | Same SQS queues / SNS fanout semantics and broker instance, with separate run namespaces |

MassTransit has no Redis Streams transport. Its Redis saga repository is not a message transport. The two in-memory implementations have different internals and are not wire-compatible. The SQS/SNS comparison is the shared-transport comparison. LocalStack is an emulator: use those results to investigate client/API behavior, not to predict AWS service throughput or latency.

MassTransit 8.5.10 is pinned as the latest Apache-licensed v8 release available when this suite was created. Its package version is an MSBuild property so another supported version can be tested deliberately. The benchmark project is isolated from shipping packages. Both contenders run in the same executable dependency graph and use the same AWS SDK/runtime versions; actual versions are embedded in every JSON result. No application contracts or messages pass between the contenders.

All cases use the same ASCII payload, producer parallelism and per-endpoint consumer limit. MassTransit prefetch is explicitly matched to that limit. Fanout uses separate durable subscription queues, and per-endpoint concurrency is reported rather than pretending that four subscriptions have the same total concurrency as one. The baseline calls the ordinary send/publish API once per input. Batch cases call each library's public batch API; native batching, pipelining and acknowledgement buffering remain part of the implementation being measured. Collection size does not imply a single atomic broker request. Default serializers/envelopes remain enabled, so equal application payload sizes do not imply equal wire bytes.

## Measurement contract

- Delivery latency starts immediately before submission and ends after broker acknowledgement. Foundatio explicitly completes its manual delivery before recording it. MassTransit's `IReceiveObserver.PostReceive` runs after `ReceiveLock.Complete`; its consumer attaches the message to the receive context for that observer. This instrumentation and payload validation are included in client CPU/allocation totals.
- Queue throughput counts unique acknowledged inputs. Pub/sub reports both inputs/second and deliveries/second: one input with four subscribers produces four expected deliveries. The denominator includes draining the last submitted work, preventing an undrained backlog from looking like throughput.
- The outstanding window bounds admitted inputs and releases an input only when all its subscriber copies have acknowledged. It is not a fire-and-forget producer benchmark. Every input/subscriber pair is tracked separately; duplicate copies cannot hide missing fanout.
- The tracking arrays are allocated before measurement. Their size is bounded by `--max-messages`; hitting that limit invalidates the trial rather than silently shortening it. Normal profiles reserve capacity for 20 million inputs; the soak profile reserves 100 million. Long/faster runs may require splitting trials. Working-set results include those fixed tracking arrays and warmup state, so compare memory usage only between trials with equal tracking capacity.
- Per-process allocated bytes, CPU time, GC collections and pauses cover producer, consumers and harness. They exclude Redis/LocalStack processes. One-second samples preserve backlog, throughput, working set and GC heap/commitment/fragmentation trends. GC memory information describes the last completed collection, not a live-object census; growing RSS alone does not prove a leak. `SendCallLatency` is per API call, so a batch of ten represents ten inputs.
- Latency histograms retain all samples, including slow tails, in fixed storage with one-microsecond resolution below 64 microseconds and at most approximately 1.6 percent bucket width above it. Percentiles use bucket upper bounds; maximum is exact to the recorded microsecond. Reported p50/p95/p99 are medians of each trial's percentiles, not a percentile formed by averaging durations.
- Saturation tests (`--rate 0`) are bounded closed-loop tests. Offered-rate tests use each input's intended schedule as its latency origin, including time waiting for producer capacity. A monotonic-clock recheck prevents early submission from timer rounding; scheduling has millisecond granularity. They expose scheduling/backpressure delay instead of hiding coordinated omission. If the configured offered rate is not achieved, report that deficit; these tests do not create an unlimited external arrival queue.
- Automatic retries and at-least-once delivery can produce duplicates. Those are reported and invalidate the performance comparison for investigation; this does not claim either library guarantees exactly-once side effects.

## Individual and offered-rate cases

```powershell
$runner = 'benchmarks/Messaging/bin/Release/net10.0/Foundatio.Messaging.Benchmarks.dll'
dotnet $runner --engine foundatio --transport redis --scenario queue --seconds 30 --warmup 5 --producers 32 --consumers 32 --prefetch 32 --outstanding 1024 --payload 1024 --output redis-queue.json

dotnet $runner --engine masstransit --transport sqs --scenario pubsub --subscribers 4 --seconds 30 --producers 8 --consumers 8 --prefetch 8 --rate 200 --outstanding 1024 --output sqs-rate200.json

# Sanity-check the shared tracking/generation overhead without serialization or a broker.
dotnet $runner --engine loopback --transport memory --scenario queue --seconds 5 --output harness-overhead.json
```

For connection overrides use `PERF_REDIS`, `PERF_AWS_URL` and `PERF_AWS_REGION`. The default SQS/SNS endpoint is local port 24566 with LocalStack test credentials. Live AWS requires explicitly setting `PERF_AWS_MODE=live`; the normal AWS SDK credential chain supplies credentials. Run from comparable client hosts and regions, and retain the exact broker/client configuration. Each invocation creates and removes its own `fperf-<random>` resources; it never purges arbitrary application queues. If interrupted before cleanup, use the prefix in its log/result to identify only that run's resources.

## References

- [MassTransit SQS/SNS configuration](https://masstransit.massient.com/configuration/transports/amazon-sqs)
- [Pinned MassTransit acknowledgement/observer ordering](https://github.com/MassTransit/MassTransit/blob/62ab339afa3bac2e9b3fe1769d0d35d7e44778e9/src/MassTransit/Transports/ReceivePipeDispatcher.cs)
- [MassTransit 8.5.10 package](https://www.nuget.org/packages/MassTransit/8.5.10)
