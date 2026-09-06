# Messaging and job runtime measurements

For sustained queue and pub/sub load tests, including Redis, SQS/SNS and MassTransit comparisons, see the [distributed messaging results](Messaging/RESULTS.md) and [reproduction instructions](Messaging/README.md).

Measured locally on September 6, 2026 with .NET 10.0.11, SDK 10.0.111, BenchmarkDotNet 0.15.8 and an AMD Ryzen AI 9 HX 470 Linux host. These are development measurements, not production sizing promises. ShortRun timing intervals are wide on this shared machine; allocation differences and removal of history-dependent work are the stronger evidence.

## Small-header construction and serialization

A temporary benchmark copied six or sixteen key/value pairs, optionally froze the dictionary, then performed six lookups. The dictionary stayed privately owned; the public MessageHeaders wrapper remains immutable and case-insensitive.

| Header count | Copy then freeze | Private dictionary | Allocated before / after |
| --- | ---: | ---: | ---: |
| 6 | 687 ns | 161 ns | 1,848 / 464 bytes |
| 16 | 1,698 ns | 245 ns | 3,288 / 992 bytes |

An eight-header serialization probe allocated 1,464 bytes when copying into another dictionary first, versus 512 bytes when serializing the existing headers directly. The implementation now serializes its private backing dictionary without the extra copy. These probes isolate the backing-store decisions; they are not a claimed end-to-end messaging speedup.

## Idle job polling

The same empty-claim benchmark ran before and after separating active state from retained history. Each store contained zero runnable jobs and either zero or 10,000 completed jobs.

| Retained jobs | Before | After | Allocated before / after |
| --- | ---: | ---: | ---: |
| 0 | 328 ns | 44 ns | 432 / 48 bytes |
| 10,000 | 175 microseconds | 52 ns | 400,512 / 48 bytes |

Previously each poll copied the ConcurrentDictionary values, including completed history. An idle worker now checks active state independently. Ready-job ordering and eligibility remain covered by shared store conformance tests.

## Local transport workloads

Five rounds of 300 messages with a 256-byte body and two headers; provisioning and one warm-up call were excluded. Each response was checked for acceptance. Redis 8.6 and LocalStack 3.8.1 ran in isolated local containers. These compare individual calls with batching on the revised implementation, not two complete PR revisions.

| Transport | Inputs per call | Median time for 300 sends | Messages/sec | Call p95 |
| --- | ---: | ---: | ---: | ---: |
| Redis | 1 | 50.5 ms | 5,938 | 0.280 ms |
| Redis | 10 | 10.0 ms | 29,906 | 0.492 ms |
| Redis | 64 | 5.20 ms | 57,717 | 1.89 ms |
| LocalStack SQS | 1 | 755 ms | 397 | 5.75 ms |
| LocalStack SQS | 10 | 219 ms | 1,372 | 11.1 ms |

Redis still executes one atomic script per message, now with bounded concurrent requests. AWS sends up to ten entries in one native batch request; partial acceptance remains visible per input. LocalStack latency does not predict live AWS latency. Network-call p95 measures an entire batch, so batch-size rows perform different amounts of work per call.

A separate in-memory receive workload processed 300 messages whose handlers awaited a two-millisecond delay. Configured concurrency 1, 8 and 32 produced observed peaks of 1, 8 and 32, taking approximately 789, 89 and 25 milliseconds. This confirms overlapping execution; shared concurrency and staggered-job-arrival regression tests protect the behavioral contract.

## Reproduce ongoing hot-path checks

The checked-in benchmarks exercise actual public APIs and are intended to catch future allocation regressions:

```powershell
dotnet run --project benchmarks -c Release -- --filter '*MessageHeadersBenchmarks*' '*JobPollingBenchmarks*' --job Dry
dotnet run --project benchmarks -c Release -- --filter '*JobPollingBenchmarks*'
```

Use identical runtime, hardware, configuration and data when comparing revisions. Live AWS, Redis Cluster/failover and sustained production load still require deployment-specific validation.
