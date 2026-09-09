# Job tracking performance — September 8, 2026

This pass optimizes the shared `IJobRuntimeStore` used for broker-delivered work. No caller API changes are required.

- Redis caches encoded index names, updates only changed status/expiry indexes, and reads only history entries that actually need removal.
- Admission combines bounded expired-history cleanup and capacity checks in one script; individual reads expire and load the requested job atomically.
- Snapshot parsing avoids temporary arrays and per-field string keys. Header builders copy only when reused after publishing a snapshot.
- Routine cancellation polling shutdown avoids throwing an exception; uncancellable operations avoid an unnecessary linked token source.

## Measurements

Median jobs/second, before (`9288e40`) versus this change, through Mediator's unchanged queue integration:

| Workload | Before | After | Improvement | Allocated bytes/job, before → after |
| --- | ---: | ---: | ---: | ---: |
| In-memory transport, Redis tracking | 1,695 | 6,153 | 3.63× | 49,721 → 47,366 |
| LocalStack SQS, Redis tracking | 1,380 | 2,597 | 1.88× | 90,257 → 85,535 |
| In-memory transport, no tracking | 91,952 | 99,679 | 1.08× | 11,279 → 9,843 |
| In-memory transport and tracking | 31,672 | 30,949 | 0.98× | 19,205 → 17,045 |

Redis acceptance p99 fell from 46.99 to 11.46 ms with the in-memory transport. In-memory tracking throughput varied across batches; a five-pair follow-up was level. Its pooled eight-run median above does not establish a throughput gain, although allocations fall 11%.

All runs used the normal Ubuntu `/usr/bin/dotnet` (.NET 10.0.11), Release, concurrency 64, a 256-character payload, and a 1,000-message warmup. Redis workloads process 10,000 messages, untracked memory 200,000, and tracked memory 50,000. Redis and LocalStack 3.8.1 run locally. Three alternating repetitions per cell, except eight native memory-tracking runs. Timing includes broker drain and verified tracked completion; startup and warmup are excluded. These are shared-host diagnostics, not production AWS capacity estimates.

[Raw Redis runs and source/binary hashes](baselines/job-tracking-2026-09-08) and the [complete comparison, latency, and all workload results](https://github.com/FoundatioFx/Foundatio.Mediator/tree/codex/core-distributed-alternative/benchmarks/Foundatio.Mediator.Distributed.Benchmarks/comparison/optimization-2026-09-08) preserve the evidence. The broader study completed 91 queue runs and 5,000,000 measured messages without missing or duplicate delivery. One additional pre-change baseline run aborted with the previously observed CLR error and was retained and repeated; no alternate runtime was used.

## Correctness

The full Foundatio build and 2,221 tests pass, with 24 expected skips. New coverage checks historical Redis records without cached index fields, progress/retry index consistency, exact retention boundaries, cleanup across 128-record admission batches, reused header-builder snapshots, and cooperative cancellation. The existing AppHost ASPIRE010 warning remains.

These changes preserve atomic capacity checks, attempt fencing, retention, and explicit broker/runtime ownership. PR #149 still has lower in-memory overhead; the full comparison reports that tradeoff.
