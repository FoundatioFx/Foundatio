# Distributed messaging performance results

The [AWS batching follow-up](AWS_BATCHING_RESULTS.md) contains the newer optimized SQS/SNS measurements. The tables below preserve the original baseline.

Measured September 6, 2026. The sustained workload exposed and helped fix excessive timer retention in the in-memory transport. Foundatio leads the concurrent in-memory cases; MassTransit leads the concurrent SQS/SNS emulator cases. Serial queues provide counterexamples to any claim of a universal winner.

141 benchmark trials are retained: 139 succeeded and 2 failed. Successful trials accounted for 170,917,727 inputs and 311,455,223 unique acknowledged deliveries. These totals include the retained before measurements and the loopback control, and exclude warmup.

Native CLR crashes remain unexplained. They are recorded below and prevent treating this work as a complete reliability qualification.

## Method and environment

- AMD Ryzen AI 9 HX 470, 24 logical processors, Linux x64, .NET 10.0.11 / SDK 10.0.111, server GC, Release builds. The client host was shared with other development services; there was no CPU affinity or dedicated host isolation.
- Redis 8.6 is a real Redis broker, with AOF enabled and `appendfsync everysec`. LocalStack 3.8.1 emulates SQS/SNS. Each broker container had a four-CPU limit; Redis had 2 GiB and LocalStack 3 GiB memory limits. Redis acknowledgement does not wait for an fsync on every input. This is not a durability-loss experiment or a live AWS benchmark.
- MassTransit 8.5.10 was pinned. Both drivers use the same executable dependency graph, runtime and AWS SDK versions. Exact versions, source revisions, options and host metadata accompany the raw trials.
- Standard cases use 1 KiB application payloads, a 1,024-input outstanding window, and three fresh-process repetitions of ten seconds of publishing. Warmup runs for up to three seconds or one million inputs. The two-minute soaks use up to five seconds of warmup and a 100-million-input tracking capacity; other profiles use 20 million.
- Concurrent queues have 32 producer workers and 32 consumer slots. Four-way fanout has 32 producers and eight consumer slots in each of four subscriptions. MassTransit prefetch matches the per-endpoint consumer limit. Native batching and default envelopes remain enabled.
- Delivery latency ends after broker acknowledgement. Throughput includes the final drain. Every input/subscriber pair is validated; duplicates cannot hide missing copies. CPU, allocations and memory include the client and harness, and exclude broker processes. All measured workers ran sequentially, without local builds or test suites competing with them.
- Tables show medians of successful trials. The [raw summaries](baselines/2026-09-06/) retain ranges and all failures; three samples do not establish statistical confidence. The in-memory tables use the repeated measurements after the timer fix; distributed baseline code was unchanged by that fix. Profile revisions remain separate.

## Concurrent queues

| Implementation | Inputs/s | p99 ms | Allocated bytes/input | Peak working set MiB |
| --- | ---: | ---: | ---: | ---: |
| Foundatio, memory | 258,087 | 5.44 | 12,474 | 121.9 |
| MassTransit, memory | 100,661 | 14.46 | 22,655 | 129.8 |
| Foundatio, Redis | 21,233 | 88.06 | 22,680 | 198.7 |
| Foundatio, SQS/SNS emulator | 715 | 1,802.24 | 21,007 | 131.6 |
| MassTransit, SQS/SNS emulator | 2,637 | 712.70 | 67,436 | 149.0 |

## Four-subscriber fanout

| Implementation | Published inputs/s | Acknowledged deliveries/s | p99 ms |
| --- | ---: | ---: | ---: |
| Foundatio, memory | 95,855 | 383,419 | 12.03 |
| MassTransit, memory | 73,940 | 295,761 | 21.50 |
| Foundatio, Redis | 7,975 | 31,899 | 159.74 |
| Foundatio, SQS/SNS emulator | 101 | 403 | 9,961.47 |
| MassTransit, SQS/SNS emulator | 420 | 1,680 | 3,375.10 |

The saturation latency includes up to 1,024 admitted inputs waiting for their acknowledgements. It is not unloaded network latency; controlled-rate results appear below.

## Timer retention fix

The old in-memory transport created a visibility-reclaim timer on every receive and renewal. Completed messages left those timers alive until their deadlines. A regression with 100 completed-and-renewed deliveries observed 200 retained timers. The fix uses one shared timer, disables its polling when idle, restarts it when deliveries arrive, and removes expired receipts only if their lease has not changed. Tests cover bounded timer resources, idle clock advancement, disposal, renewed leases and waking blocked receivers.

| Workload | Before inputs/s | After inputs/s | Before p99 ms | After p99 ms | Before peak MiB | After peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Concurrent queue | 213,749 | 258,087 | 16.89 | 5.44 | 1,326.4 | 121.9 |
| Four-subscriber fanout | 87,561 | 95,855 | 23.17 | 12.03 | 1,944.9 | 123.4 |

The before fanout median has two successful trials and one native process crash. All three repeated after trials succeeded. This establishes the timer-resource improvement; it does not establish the cause of the native crashes.

## Serial queues, larger payloads and batches

Serial means one producer worker and one consumer slot, with the same outstanding window. It is not a one-message-at-a-time round-trip test.

- Serial memory: Foundatio 112,277 inputs/s; MassTransit 122,978 inputs/s.
- Serial SQS/SNS emulator: Foundatio 406 inputs/s; MassTransit 282 inputs/s.

Extended cases have one trial each and should be treated as exploratory. Batch cases use eight producer workers and ten inputs per API call; 16 KiB cases use 32 producers and single-input calls. The producer-count change means these are not isolated A/B estimates of batching alone.

| Implementation | 16 KiB queue inputs/s | 16 KiB fanout inputs/s | Batch-10 queue inputs/s | Batch-10 fanout inputs/s |
| --- | ---: | ---: | ---: | ---: |
| Foundatio, memory | 79,281 | 59,583 | 282,546 | 98,545 |
| MassTransit, memory | 48,866 | 39,049 | 98,847 | 76,375 |
| Foundatio, Redis | 7,729 | 3,170 | 20,221 | 7,286 |
| Foundatio, SQS/SNS emulator | 646 | 91 | 1,117 | 109 |
| MassTransit, SQS/SNS emulator | 1,261 | 303 | 2,757 | 500 |

## Two-minute soaks

| Trial | Inputs | Inputs/s | p99 ms | Peak working set MiB | Missing / duplicates |
| --- | ---: | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub | 11,701,265 | 97,507 | 11.65 | 172.6 | 0 / 0 |
| foundatio/memory queue | 31,348,296 | 261,223 | 5.50 | 238.7 | 0 / 0 |
| round1-foundatio-redis-pubsub-four.json | FAILED | | | | |
| foundatio/redis queue | 2,557,096 | 21,302 | 88.06 | 201.1 | 0 / 0 |
| foundatio/sqs pubsub | 12,723 | 98 | 11,403.26 | 133.4 | 0 / 0 |
| foundatio/sqs queue | 90,903 | 753 | 1,703.93 | 128.5 | 0 / 0 |
| masstransit/memory pubsub | 9,025,543 | 75,202 | 22.02 | 216.4 | 0 / 0 |
| masstransit/memory queue | 12,042,135 | 100,338 | 14.59 | 173.1 | 0 / 0 |
| masstransit/sqs pubsub | 54,104 | 446 | 2,818.05 | 153.8 | 0 / 0 |
| masstransit/sqs queue | 322,970 | 2,686 | 704.51 | 153.2 | 0 / 0 |
| foundatio/redis pubsub (repeat) | 954,234 | 7,947 | 169.98 | 212.3 | 0 / 0 |

The soak tracking arrays are larger than the short-run arrays. Their pages become resident as sequence numbers advance, so RSS growth can reflect the tracker being touched. GC heap/commitment/fragmentation samples describe the last completed collection and do not count only live application objects. Inspect the raw time series before calling a trend a leak.

## Controlled arrival rates

These thirty-second trials timestamp each input at its intended schedule, including capacity delay. The timer rechecks a monotonic clock to prevent early sends from sub-millisecond rounding. Arrival scheduling has millisecond granularity. Admission below target is shown explicitly; the generator bounds outstanding work instead of maintaining an unlimited external arrival queue.

| Trial | Target inputs/s | Admitted inputs/s | Acknowledged inputs/s including drain | p99 ms |
| --- | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub | 50,000 | 50,000.4 | 49,998.3 | 2.85 |
| foundatio/memory queue | 50,000 | 50,000.1 | 49,998.9 | 1.63 |
| foundatio/redis pubsub | 10 | 10.0 | 10.0 | 103.42 |
| foundatio/redis pubsub | 3,000 | 2,999.9 | 2,997.9 | 27.39 |
| foundatio/redis queue | 10 | 10.0 | 10.0 | 103.42 |
| foundatio/redis queue | 3,000 | 3,000.0 | 2,997.9 | 27.14 |
| foundatio/sqs pubsub | 10 | 10.0 | 10.0 | 28.41 |
| foundatio/sqs pubsub | 100 | 100.0 | 88.2 | 4,325.38 |
| foundatio/sqs queue | 10 | 10.0 | 10.0 | 10.62 |
| foundatio/sqs queue | 100 | 100.0 | 100.0 | 8.96 |
| masstransit/memory pubsub | 50,000 | 49,999.7 | 49,990.1 | 6.46 |
| masstransit/memory queue | 50,000 | 49,999.9 | 49,988.9 | 2.24 |
| masstransit/sqs pubsub | 10 | 10.0 | 10.0 | 30.98 |
| masstransit/sqs pubsub | 100 | 100.0 | 99.9 | 430.08 |
| masstransit/sqs queue | 10 | 10.0 | 10.0 | 11.78 |
| masstransit/sqs queue | 100 | 100.0 | 100.0 | 9.73 |

## Harness control and failures

- Loopback pubsub: 1,242,226 inputs/s, 80 allocated bytes/input. This includes generation and validation without serialization or a broker; its costs remain included in every library result.
- Loopback queue: 1,466,421 inputs/s, 80 allocated bytes/input. This includes generation and validation without serialization or a broker; its costs remain included in every library result.
- Retained failed benchmark trial: `standard/round2-foundatio-memory-pubsub-four.json`. Worker exited without a result. RUN fperf-6b4027ac3eba foundatio/memory/pubsub
- Retained failed benchmark trial: `soak/round1-foundatio-redis-pubsub-four.json`. Worker exited without a result. RUN fperf-fd29ab56ca9c foundatio/redis/pubsub
- Two additional exploratory workers, one Foundatio in-memory queue and one Foundatio Redis queue, terminated with native CLR error `0x80131506` before the final profile sequence. Dump collection was enabled after the first occurrence. Native dumps are retained locally and are not committed to the repository. Their root cause is unconfirmed.
- Initial MassTransit fanout experiments received traffic from previous runs because explicitly named queues/topics bypassed the configured namespace. Both names now include the run prefix. Repeated queue/fanout verification finished with zero SQS queues and zero SNS topics; contaminated experiments are excluded from comparisons.
- A timer regression reproduced early scheduled submission, then passed after a monotonic-clock recheck was added. No controlled-rate results here use the earlier implementation.

## Validation

The full Release solution build succeeded with the existing AppHost ASPIRE010 warning. All 2,138 regression tests completed: 2,114 passed and 24 expected skips, with zero failures. This includes the Redis and AWS suites against the isolated brokers and seven measurement tests. The documentation build, PowerShell parsing, nonempty-output-directory guard and archived measurement invariants passed. These regression results do not erase the separate native benchmark failures.

## Recommended next work

1. Investigate the retained native CLR crashes before declaring release readiness. Passing later trials does not identify their cause.
2. Measure bounded automatic SQS/SNS batching as the next transport optimization. Foundatio currently deletes each completed SQS message individually. The pinned MassTransit implementation coalesces sends, publishes and deletes. Preserve per-input results, cancellation, actual acknowledgement completion and low-rate latency while testing any change. This is a source-based optimization hypothesis, not an isolated causal experiment.
3. Set an explicit Redis idle-latency budget. At ten inputs/second, these queue and fanout trials had p99 near 103 ms, versus about 27 ms at 3,000 inputs/second. The transport starts at a 25 ms poll interval and backs off up to one second when idle. Compare a tighter cap or a wake-up mechanism against idle CPU and broker request cost before changing defaults.
4. Repeat on a dedicated client host against live AWS in the same region, including several offered rates and longer runs. LocalStack numbers describe this emulator and client pipeline; they cannot size AWS. Keep the serial, fanout, payload and low-rate cases so an improvement in saturation throughput does not hide a usability regression.

[Reproduction instructions and measurement contract](README.md) · [Raw trials, metadata and summaries](baselines/2026-09-06/) · [Pinned MassTransit queue batching](https://github.com/MassTransit/MassTransit/blob/62ab339afa3bac2e9b3fe1769d0d35d7e44778e9/src/Transports/MassTransit.AmazonSqsTransport/AmazonSqsTransport/QueueInfo.cs) · [Pinned MassTransit topic batching](https://github.com/MassTransit/MassTransit/blob/62ab339afa3bac2e9b3fe1769d0d35d7e44778e9/src/Transports/MassTransit.AmazonSqsTransport/AmazonSqsTransport/TopicInfo.cs)
