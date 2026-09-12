# Messaging and job tracking — September 12, 2026

This pass removes lease-monitoring work from short deliveries while preserving renewal, expiry, cancellation and settlement races. The asynchronous monitor starts at the first scheduled lease check; a timer supervises the delivery from admission. Six new regressions cover its lifecycle.

External project references now preserve Release/Debug configuration through the entire graph. Previously a Release Mediator solution build could copy Debug native dependencies. The integration's CI smoke check now rejects unoptimized benchmark assemblies.

## Performance

Fresh-process medians on system .NET 10.0.12, Release, comparing the previous native implementation with this change:

| Workload | PR #149 jobs/s | Before jobs/s | After jobs/s | Allocated bytes/job, before → after |
| --- | ---: | ---: | ---: | ---: |
| In memory, concurrency 64 | 181,195 | 95,696 | 100,346 | 8,358 → 7,928 |
| In memory, tracked, concurrency 64 | 36,658 | 33,238 | 34,111 | 15,535 → 15,146 |
| In memory, concurrency 1 | 110,805 | 93,045 | 100,537 | 8,686 → 8,271 |
| In memory, concurrency 8 | 177,069 | 84,916 | 91,161 | 8,340 → 7,934 |
| SQS / LocalStack, concurrency 64 | 2,788 | 3,065 | 2,968 | 47,026 → 46,623 |
| In memory + Redis tracking, concurrency 64 | 10,619 | 7,757 | 7,600 | 45,852 → 45,450 |
| SQS / LocalStack + Redis tracking, concurrency 64 | 2,702 | 2,653 | 2,582 | 84,285 → 83,930 |

Untracked in-memory throughput improves **5–8%**, allocations fall **about 5%**, and concurrency-64 process CPU falls **17%**. Longer tracked-memory runs are level. Redis tracking is **4% slower** in five longer alternating pairs, with **5% less CPU** and **1% fewer allocated bytes**; no Redis/SQS speedup is claimed. Default 1 ms receive collection improves about **7%**. The bus-only diagnosis drops from **4,870 to 4,250 bytes/delivery**; layer timings are not independently subtractable costs.

The main matrix verifies **4.32M jobs** in 63 runs. Thirty longer/default checks verify another **3.2M**. Each matrix cell uses three rotating trials, 1,000 warmup messages and a 256-character payload. Timing includes broker drain and tracked completion. LocalStack is not production AWS capacity. [Method, latency, CPU, raw data, source fingerprints and excluded mixed-build trials](https://github.com/FoundatioFx/Foundatio.Mediator/tree/codex/core-distributed-alternative/benchmarks/Foundatio.Mediator.Distributed.Benchmarks/comparison/production-pass-2026-09-12). [Raw native-side data](baselines/job-tracking-production-2026-09-12) and [previous measurements](baselines/job-tracking-pass2-2026-09-08).

## Correctness

Full build and **2,235 Foundatio tests pass**, with 24 existing skips and the existing AppHost ASPIRE010 warning. **756 Mediator tests**, 23 browser scenarios, Quickstart, console, frontend and docs checks pass. Mediator core still matches main exactly.

A ten-minute LocalStack/Redis run accepted **69,354 jobs**; **69,314 completed** and **40 were intentionally cancelled**. All **32** deliveries interrupted by a worker crash retried after replacement; another worker restarted gracefully during arrivals. Nothing remained pending or failed, and no duplicate effects were observed. The harness implements idempotent effects and does not claim exactly-once delivery. Real AWS deployment validation and a longer staging soak remain release work.
