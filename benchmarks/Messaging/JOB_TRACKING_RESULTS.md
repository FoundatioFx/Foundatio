# Job tracking and messaging performance — September 8, 2026

This follow-up reduces per-message work without changing application APIs. It builds on Foundatio `54006ac7` and Mediator `ff9c155`; measured source/binary fingerprints are in the linked raw data. Mediator core still matches main `a148013`.

- Single-message sends avoid successful-batch bookkeeping while preserving acceptance uncertainty and provider failure details.
- Header builders share immutable snapshots until an edit; automatic settlement and unused lease renewal avoid allocating semaphores.
- In-memory receipts use object identity instead of generating a GUID for every delivery. Stale receipts still cannot settle or renew a redelivery.
- Redis cancellation combines targeted expiry and the cancellation read in one atomic operation.
- The Mediator extension caches immutable registration metadata and skips empty provider enumeration; ordinary middleware and scoped dependencies still run per invocation.

## Measurements

Median jobs/second through the native Mediator integration, system .NET 10.0.11, Release:

| Workload | Before | After | Allocated bytes/job, before → after |
| --- | ---: | ---: | ---: |
| In memory, concurrency 1 | 81,347 | 87,539 | 10,195 → 8,685 |
| In memory, concurrency 8 | 80,149 | 84,685 | 9,852 → 8,333 |
| In memory, concurrency 64 | 100,073 | 97,236 | 9,847 → 8,353 |
| In memory and tracking, concurrency 64 | 31,049 | 30,584 | 17,049 → 15,533 |
| LocalStack SQS, concurrency 64 | 2,809 | 2,846 | 48,011 → 47,013 |
| In memory + Redis tracking, concurrency 64 | 5,898 | 5,409 | 47,360 → 45,831 |
| LocalStack SQS + Redis tracking, concurrency 64 | 2,498 | 2,445 | 85,479 → 84,388 |

Untracked memory allocations fall **15%**, memory tracking **9%**, and Redis tracking **3%**. Throughput improves **8% at concurrency 1** and **6% at concurrency 8**. High-concurrency memory and LocalStack are near the baseline; this pass does not establish a throughput gain there.

Redis short runs varied: an exploratory batch favored the change, while the table measured 8.3% lower throughput. Five additional alternating pairs of **30,000 Redis-tracked jobs** measured **6,380 → 6,503 jobs/s**, **47,323 → 45,839 bytes/job**, and **16,474 → 14,290 ms process CPU** (13% less). Acceptance p99 was 11.34 → 10.71 ms. The evidence supports lower allocation/CPU cost, with no consistent throughput gain. The longer runs are separate evidence and do not replace the table's shorter workload.

Each table cell has three rotating fresh-process repetitions, a 1,000-message warmup and a 256-character payload. Counts: 200,000 memory/concurrency 64; 100,000 at concurrency 1/8; 50,000 memory tracked; 10,000 with Redis and/or LocalStack. Timing includes broker drain and retained tracked completion. Startup, warmup and shutdown are excluded. Redis 7 and LocalStack 3.8.1 ran locally on the same shared Linux host; LocalStack does not estimate production AWS capacity.

[Raw data and fingerprints](baselines/job-tracking-pass2-2026-09-08) and the [complete Mediator comparison](https://github.com/FoundatioFx/Foundatio.Mediator/tree/codex/core-distributed-alternative/benchmarks/Foundatio.Mediator.Distributed.Benchmarks/comparison/optimization-pass2-2026-09-08) retain latency, PR #149 results and exploratory batches. PR #149 remains faster and leaner in memory. The preceding index/history optimization remains documented in [its original report](https://github.com/FoundatioFx/Foundatio.Mediator/tree/codex/core-distributed-alternative/benchmarks/Foundatio.Mediator.Distributed.Benchmarks/comparison/optimization-2026-09-08).

## Correctness and recovery

The full Foundatio build and **2,229 tests pass**, with 24 existing skips and the existing AppHost ASPIRE010 warning. Mediator builds with zero warnings and **756 tests pass**. Added coverage checks cancellation at exact history expiry, partial/malformed send results, immutable snapshots, overlapping settlement, stale receipt settlement/renewal, and repeated scoped header restoration.

The final comparison completed **63 successful runs and 4.32M measured jobs** without missing or duplicate delivery. One additional PR #149 trial and one Mediator build hit the previously observed CLR abort and passed their same-runtime retries; failures remain in the evidence. No runtime installation changed.

A separate two-minute LocalStack/Redis arrival test accepted **46,504 jobs**: **46,464 completed**, **20 cancelled while queued**, and **20 while running**. A process was killed with **32 handlers in flight**; all 32 retried after replacement. Another worker gracefully stopped and restarted while arrivals continued. No pending, failed, dead-lettered or acceptance-unknown jobs remained. The application effect was idempotent, with zero duplicate effect attempts observed; delivery remains at least once. The linked full report includes the reproducible recovery harness.
