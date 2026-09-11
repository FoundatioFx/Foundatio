# AWS automatic batching performance follow-up

Automatic batching improves concurrent SQS queues by 3.2 times, one-subscriber SNS/SQS pub/sub by 4.0 times, and four-subscriber fanout by 3.3 times versus the preserved implementation. In this LocalStack comparison, Foundatio is faster than MassTransit on serial queues, approximately tied on one-subscriber pub/sub, 10% behind on concurrent queues and 28% behind on four-subscriber fanout. These are measured results, not a claim that Foundatio is universally faster.

All 54 final trials succeeded: 1,113,065 measured inputs and 1,522,595 acknowledged deliveries, with zero missing, duplicate or invalid deliveries. The earlier 36-case batching comparison and six-case refinement check are retained separately; together these 96 trials validated 1,982,868 deliveries. No native worker crashes occurred in this follow-up. The unresolved native CLR failures in the [earlier baseline](RESULTS.md#harness-control-and-failures) remain a release blocker.

## Repeated comparison

Three fresh-process repetitions per cell, ten seconds of publishing after up to three seconds of warmup. Values are medians; throughput includes final acknowledgement and drain. Payload is 1 KiB and the outstanding window is 1,024 inputs. Concurrent queues use 32 producer workers and 32 consumer slots; fanout uses 32 producers and eight slots in each of four subscriptions. Serial means one producer worker and one consumer slot, with the same outstanding window; it is not a one-at-a-time round-trip test. MassTransit prefetch equals its per-endpoint consumer limit.

| Workload | Before inputs/s | After inputs/s | MassTransit inputs/s | After / before |
| --- | ---: | ---: | ---: | ---: |
| Serial queue | 398 | 385 | 294 | 0.97x |
| Concurrent queue | 736 | 2,328 | 2,584 | 3.16x |
| One-subscriber pub/sub | 324 | 1,295 | 1,293 | 4.00x |
| Four-subscriber fanout | 97 | 317 | 439 | 3.26x |

Serial throughput is 3% below its previous median, with overlapping observed ranges (before 383–414 inputs/s; after 384–397). The refinement recovered the large serial regression in the first batching candidate: always waiting for partial acknowledgement batches reduced its median to 277 inputs/s. Idle streams of singleton batches now skip repeated collection waits.

| Workload | Before p99 ms | After p99 ms | MassTransit p99 ms |
| --- | ---: | ---: | ---: |
| Serial queue | 2,750.25 | 2,697.86 | 3,203.50 |
| Concurrent queue | 1,668.59 | 785.79 | 729.09 |
| One-subscriber pub/sub | 4,194.30 | 1,097.73 | 1,040.38 |
| Four-subscriber fanout | 10,542.04 | 4,063.23 | 3,145.73 |

Saturation latency includes the bounded backlog. It is not unloaded service latency. Three samples and a shared host do not establish statistical significance for small differences; the observed throughput ranges are retained in the per-revision summaries.

## What changed

- Ordinary concurrent send, publish and complete calls now coalesce into native AWS requests. Application code keeps using the single-message API. Per-entry outcomes and broker message IDs remain attached to the correct caller.
- Automatic batching defaults to ten entries, at most 100 buffered operations and four active requests per destination and operation. Additional callers await capacity. Encoded bytes are bounded separately: SQS 1 MiB, SNS 256 KiB. Partial batches collect for up to one millisecond; idle singleton streams skip repeated waits. Shared requests and disposal drains have a 30-second timeout. Explicit batch calls retain their existing chunked behavior.
- Acknowledgement waits for the broker response. Missing, failed or invalid delete results do not report success. Canceling one caller cannot cancel other messages sharing its request; an uncertain send remains unknown. Disposal drains admitted work before owned SDK clients are disposed.
- AWS advertises its ten-message receive limit. The core briefly collects freed slots before another small pull, while maintaining a strict per-delivery concurrency budget and allowing a completed delivery to free its slot independently of slower handlers. Other providers retain zero receive delay.
- The batch worker does not inherit the first caller's async context. A regression reproduced retention of that request-scoped state on subsequent calls and now passes. Wire encoding, native headers and normal lease supervision remain enabled.

## Broker request evidence

These six additional ten-second trials have no warmup. Counts come from LocalStack operation logs and therefore also include startup, drain and cleanup. The table selects only send/publish, receive and delete operations. Receive counts can include empty polls. Entries per request are calculated from the fully validated measured input/delivery counts.

| Workload / implementation | Send or publish entries/request | Deliveries/receive request | Receipts/delete request |
| --- | ---: | ---: | ---: |
| Concurrent queue / before | 1.00 | 9.99 | 1.00 |
| Concurrent queue / after | 6.97 | 9.98 | 9.91 |
| Concurrent queue / masstransit | 8.14 | 8.15 | 8.09 |
| Four-subscriber fanout / before | 1.00 | 3.58 | 1.00 |
| Four-subscriber fanout / after | 5.14 | 6.68 | 6.48 |
| Four-subscriber fanout / masstransit | 7.63 | 7.99 | 7.81 |

The original code used the batch-send endpoint with one entry and deleted each receipt separately. The optimized queue case averages 6.97 entries per send and 9.91 receipts per delete. This directly verifies that ordinary API calls now amortize broker requests.

For fanout, Foundatio averages 5.14 entries per publish and 6.48 receipts per delete, versus MassTransit's 7.63 and 7.81. That implies about 48% more publish requests and 21% more delete requests for equal work. This is evidence that batch utilization remains an optimization target; it does not isolate every source of the throughput gap. The next focused experiment should improve fanout batch collection without making fast handlers wait indefinitely for a slow handler, then confirm the result against live AWS.

## Controlled arrival rates

One thirty-second trial per cell, timestamped at the intended arrival schedule. All cases admitted their target rate to rounding; the acknowledgement denominator includes final drain. These are latency checks, not repeated confidence estimates.

| Target inputs/s | Workload | Foundatio p99 ms | MassTransit p99 ms |
| ---: | --- | ---: | ---: |
| 10 | Concurrent queue | 9.98 | 11.65 |
| 10 | Four-subscriber fanout | 27.65 | 29.18 |
| 100 | Concurrent queue | 8.00 | 8.96 |
| 100 | Four-subscriber fanout | 438.27 | 479.23 |

## Two-minute soaks

| Implementation / workload | Inputs | Inputs/s | p99 ms | Peak working set MiB |
| --- | ---: | ---: | ---: | ---: |
| after/fanout | 36,791 | 304 | 4,194.30 | 128.9 |
| after/queue | 293,275 | 2,439 | 737.28 | 125.3 |
| masstransit/fanout | 52,464 | 434 | 2,916.35 | 165.8 |
| masstransit/queue | 318,875 | 2,652 | 712.70 | 151.6 |

All four soaks ran the full 120-second publishing window, used five seconds of warmup, and drained every delivery. The same 20-million-input tracking capacity was retained across this follow-up; these memory figures should not be directly compared with the older baseline's larger soak tracker.

## Client cost

| Workload | Before allocated bytes/input | After allocated bytes/input | MassTransit allocated bytes/input |
| --- | ---: | ---: | ---: |
| Serial queue | 131,191 | 138,928 | 185,334 |
| Concurrent queue | 109,022 | 59,292 | 67,454 |
| One-subscriber pub/sub | 148,868 | 40,829 | 80,948 |
| Four-subscriber fanout | 377,168 | 190,645 | 84,765 |

Allocation and CPU counts include the client and harness, and exclude LocalStack. Fanout allocations remain higher than MassTransit even though Foundatio's median client CPU time per input is lower (1.42 versus 1.74 ms). RSS includes fixed delivery-tracking arrays; samples and GC statistics are retained and are not a live-object census.

## Reproduction and validation

- Optimized shipping implementation: `abce1c0e`. Preserved before checkout: `5dae40ef`; its unchanged shipping assemblies carry `95983490` informational metadata. The intermediate batching candidate was `1a9f0e62`. Binary SHA-256 manifests distinguish all measured executables.
- MassTransit 8.5.10, identical AWS SDK dependencies, .NET 10.0.11, server GC, Linux x64, AMD Ryzen AI 9 HX 470 / 24 logical processors. LocalStack 3.8.1 used the task-owned loopback endpoint with a four-CPU / 3-GiB container limit. The host was shared with other development services, without CPU affinity. Measured workers ran sequentially; no local builds, tests or profiling ran alongside them.
- Final standard cases were shuffled and interleaved across before, after and MassTransit with seed 534. Rate, soak and request-accounting profiles ran afterward against the same broker. All benchmark queues/topics were absent at the end, and the temporary broker containers were removed.
- The full Release solution build passed with only the existing ASPIRE010 warning. Core: 2,032 passed / 12 skipped; AWS: 39 passed / 8 skipped; Redis: 56 passed / 4 skipped; benchmark measurement: 16 passed. Total: **2,143 passed, 24 expected skips, zero failures**. Summary regressions and the documentation build passed. Tests cover mixed outcomes, byte limits, cancellation within a confirmed shared batch, bounded admission, timeout recovery, disposal, caller-context isolation and consumer slot ownership.
- The [benchmark README](README.md) documents both LocalStack and explicit live AWS mode. No actual AWS account was contacted; these emulator figures do not predict AWS throughput or latency.
- [Raw trials, per-revision summaries, manifests and scripts](baselines/2026-09-06-aws-batching/) are retained. The archive contains all 96 comparison/refinement trials, including the intermediate candidate, with a profile manifest. Earlier short diagnostic experiments are retained separately in the local handoff and are excluded from the final performance conclusions.
