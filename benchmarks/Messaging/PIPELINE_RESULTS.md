# Messaging performance and reliability follow-up

This follow-up improves ordinary queue and pub/sub operations without requiring applications to call batch APIs or tune transport internals. Consumer concurrency remains a strict bound. Sends and publishes still wait for broker acceptance, while settlement waits for the individual broker acknowledgement. Lease supervision, cancellation, retry and shutdown behavior remain enabled.

## Repeated standard comparison

| Transport / workload | Foundatio inputs/s | MassTransit inputs/s | Throughput ratio | Foundatio p99 ms | MassTransit p99 ms |
| --- | --- | --- | --- | --- | --- |
| Memory / serial | 234,753 | 127,380 | 1.84x | 6.01 | 12.03 |
| Memory / queue | 262,812 | 102,870 | 2.55x | 5.63 | 14.59 |
| Memory / fanout | 170,397 | 75,645 | 2.25x | 8.13 | 20.73 |
| LocalStack / serial | 402 | 284 | 1.42x | 2,490.37 | 3,260.86 |
| LocalStack / queue | 3,019 | 2,425 | 1.25x | 638.98 | 794.62 |
| LocalStack / pubsub-one | 1,536 | 1,131 | 1.36x | 966.65 | 1,196.03 |
| LocalStack / fanout | 425 | 423 | 1.00x | 2,850.82 | 3,506.18 |

The four-subscriber LocalStack fanout medians are effectively tied: the small difference falls within overlapping observed ranges (Foundatio 389–430; MassTransit 402–424 inputs/s). In-memory and other standard AWS medians favor Foundatio. Across the final 93 trials, 82,408,651 inputs produced 215,044,147 independently validated acknowledged deliveries, with zero missing, duplicate or invalid deliveries and no worker failures. These results establish performance for this matrix and environment, not universal dominance across workloads, brokers or latency/throughput objectives.

## Preserved implementation comparison

| Transport / workload | Before inputs/s | After inputs/s | Ratio |
| --- | --- | --- | --- |
| Memory / serial | 121,746 | 234,753 | 1.93x |
| Memory / queue | 259,229 | 262,812 | 1.01x |
| Memory / fanout | 158,447 | 170,397 | 1.08x |
| Redis / queue | 20,970 | 21,179 | 1.01x |
| Redis / fanout | 7,837 | 7,979 | 1.02x |

Memory concurrent queues and both Redis cases are close to their previous throughput; the larger gains are in memory serial queues and AWS. The earlier same-runtime AWS comparison is retained separately as `official-aws`; it measured the intermediate `d07031ff` implementation against the preserved build and MassTransit. Do not merge that intermediate comparison into the final repetitions.

## Payload and batch checks

One fifteen-second trial per cell, with three seconds of warmup. These are checks of additional workloads, not repeated confidence estimates.

| LocalStack case | Foundatio inputs/s | MassTransit inputs/s | Foundatio p99 ms | MassTransit p99 ms |
| --- | --- | --- | --- | --- |
| 16 KiB / queue | 2,148 | 1,849 | 770.05 | 868.35 |
| 16 KiB / fanout | 314 | 296 | 4,325.38 | 4,456.45 |
| Batch of ten / queue | 2,966 | 2,552 | 737.28 | 696.32 |
| Batch of ten / fanout | 467 | 446 | 3,309.57 | 2,719.74 |

Explicit batches use eight producer workers and ten inputs per API call. The batch API comparisons favor Foundatio throughput, but the sampled batch p99 values favor MassTransit. Throughput gains do not imply winning every latency statistic.

## Offered rates and round trips

One twenty-second trial per offered-rate cell. Latency starts at the intended schedule, including capacity waits. Both implementations achieved the configured offered rates to rounding.

| Target inputs/s / workload | Foundatio completed/s | MassTransit completed/s | Foundatio p99 ms | MassTransit p99 ms |
| --- | --- | --- | --- | --- |
| 10 / queue | 10.0 | 10.0 | 10.11 | 11.13 |
| 10 / fanout | 10.0 | 10.0 | 27.90 | 32.00 |
| 100 / queue | 100.0 | 100.0 | 7.36 | 9.47 |
| 100 / fanout | 99.8 | 99.8 | 425.98 | 458.75 |

With one input outstanding, one producer and one consumer, the repeated queue round-trip medians were 390 completed inputs/s and 4.42 ms p99 for Foundatio, versus 213 inputs/s and 6.46 ms for MassTransit. This window-one test is separate from the standard serial saturation case.

## Two-minute soaks

One 120-second publishing window per cell after five seconds of warmup; every admitted input is drained and validated. All soak variants reserve the same 100-million-input tracking capacity, compared with 20 million for the shorter profiles. RSS therefore must not be compared directly across these profile types.

| Transport / implementation / workload | Inputs | Inputs/s | p99 ms | Peak working set MiB |
| --- | --- | --- | --- | --- |
| aws / after / fanout | 52,517 | 436 | 2,949.12 | 131.4 |
| aws / after / queue | 368,559 | 3,066 | 663.55 | 137.6 |
| aws / masstransit / fanout | 53,328 | 441 | 2,818.05 | 156.2 |
| aws / masstransit / queue | 317,017 | 2,637 | 729.09 | 154.6 |
| memory / after / fanout | 21,320,538 | 177,662 | 6.78 | 218.3 |
| memory / masstransit / fanout | 9,081,729 | 75,674 | 21.76 | 223.9 |
| redis / after / fanout | 990,632 | 8,251 | 165.89 | 226.9 |
| redis / after / queue | 2,585,387 | 21,537 | 88.06 | 124.1 |

## Client cost

| Transport / workload | Foundatio bytes/input | MassTransit bytes/input | Foundatio CPU ms/input | MassTransit CPU ms/input |
| --- | --- | --- | --- | --- |
| Memory / serial | 11,905 | 19,673 | 0.028 | 0.028 |
| Memory / queue | 12,031 | 22,653 | 0.035 | 0.036 |
| Memory / fanout | 26,493 | 65,948 | 0.085 | 0.116 |
| LocalStack / serial | 129,701 | 168,693 | 1.162 | 1.934 |
| LocalStack / queue | 24,672 | 67,514 | 0.359 | 0.564 |
| LocalStack / pubsub-one | 38,781 | 62,487 | 0.439 | 0.789 |
| LocalStack / fanout | 126,979 | 86,962 | 1.073 | 1.831 |

AWS fanout allocation remains higher for Foundatio. The 16 KiB checks also allocate more for Foundatio: 274,385 versus 240,259 bytes/input for queues, and 619,397 versus 407,487 for fanout. Client allocation, end-to-end throughput and tail latency are separate measurements; no across-the-board allocation claim is made.

## Changes

- Receive-slot collection dispatches as soon as its batch fills. AWS can overlap up to four receives under one shared consumer budget; collection is serialized so simultaneous receivers do not split a useful batch into tiny requests.
- A settled handler releases its delivery capacity independently of slow cancellation callbacks. Deferred cancellation and lease cleanup have a separate bounded budget and are drained on shutdown.
- Ordinary AWS sends, publishes and acknowledgements coalesce automatically. Partial operation batches wait up to two milliseconds, subject to timer scheduling. SQS sends can dispatch immediately when idle; singleton streams skip repeated idle waits. Acknowledgements learn the maximum requested receive batch size, so an eight-slot consumer does not wait for two additional receipts. Already queued receipts can still fill the native ten-entry batch. Slow handlers retain the bounded partial-batch timeout.
- Batch collection no longer creates a cancellation exception for each timer expiry; normal lease-timer cancellation also avoids exception handling. Batch responses still validate individual outcomes, caller cancellation cannot cancel another caller's shared request, and admitted work drains before owned SDK clients are disposed.
- A versioned `fnd.envelope` AWS attribute carries the ID, content type, encoding and all headers. JSON/text bodies stay readable and binary bodies use base64. An empty-by-default `NativeMessageHeaders` collection can duplicate up to nine selected headers for SNS attribute filters. Names are validated and snapshotted at construction.
- Benchmark reports fingerprint the actual CoreCLR binary. `run.ps1 -DotnetPath` selects the worker host, and the summarizer rejects mixed runtime builds even when their displayed version matches.

The AWS wire/default-header change intentionally affects the unreleased provider: new readers accept the previous envelope format, but previous experimental readers cannot read new sends. Upgrade endpoints together or use a new resource prefix. Existing native SNS attribute filters must select their header names explicitly; application handlers continue receiving all headers.

## Measurement method

The final implementation is `77c20ea354919fd25ae300e49c5de7f3ed8da598`. Preserved before binaries are the previous automatic-batching implementation, `abce1c0e`, from checkout `466e3987`. Binary manifests identify all executables. MassTransit is 8.5.10, pinned to `62ab339afa3bac2e9b3fe1769d0d35d7e44778e9`, using the same AWS SDK assemblies.

All confirmed trials invoke Microsoft's official .NET 10.0.11 runtime with CoreCLR SHA-256 `3ebe90cd92b1edf6742a41fa921a0c6326216fd1cca45fdb5e055bea33351bea`, server GC, on the same shared Linux x64 host (Ubuntu 26.04.1, AMD Ryzen AI 9 HX 470, 24 logical processors). LocalStack 3.8.1 is limited to four CPUs and 3 GiB; Redis 8.6 to four CPUs and 2 GiB, with AOF everysec. Workers run sequentially in seeded shuffled order, with no concurrent builds, tests or profiling. The host is shared and has no CPU affinity; small differences and overlapping ranges should be treated cautiously.

Standard cases have three fresh-process repetitions, ten seconds of publishing after up to three seconds of warmup, 1 KiB payloads and a 1,024-input outstanding window. Concurrent queues and one-subscriber pub/sub use 32 producers and 32 consumer slots. Four-subscriber fanout uses 32 producers and eight consumer slots per subscription. MassTransit prefetch equals its per-endpoint consumer limit. Serial queues use one producer and one consumer with the same outstanding window; the separate window-one profile measures one-at-a-time round trips.

Throughput includes final acknowledgement and drain. Fanout rates count inputs; each input requires four independently validated acknowledged deliveries. Saturation p99 includes the bounded backlog and is not unloaded latency. Offered-rate latency starts at the intended arrival schedule and includes capacity waits. Counts and CPU/allocations include client and harness, excluding broker processes. RSS includes fixed tracker arrays and can vary with the portion touched during a run; comparisons must use the same tracker capacity and cannot by themselves establish a live-object leak.

Baseline harnesses predate the CoreCLR fingerprint field; their exact official-runtime invocation and DLL hashes are preserved in each profile manifest. Earlier Ubuntu-runtime experiments are retained separately and are excluded from final same-runtime comparisons.

## Runtime investigation

Earlier benchmark workers exited with native CLR error `0x80131506`. The system SDK build also terminated with that error during this follow-up. This is separate from a managed assertion or delivery-validation failure. The installed Ubuntu .NET 10.0.11 runtime links external libunwind 1.8.3, whereas Microsoft's same-version runtime does not. The Ubuntu package predates a concurrent-unwinding fix discussed in the [upstream runtime issue](https://github.com/dotnet/runtime/issues/130577) and [libunwind change](https://github.com/libunwind/libunwind/pull/993).

A standalone .NET console program with no Foundatio dependencies repeatedly threw/caught exceptions with 24 workers. Five five-second trials per runtime produced no crash in either build. The official runtime processed approximately 6.8 times as many exceptions. This demonstrates a material runtime difference, but does not reproduce or prove the cause of the historical crashes. Both contenders now use the same official runtime for confirmation; no system runtime was replaced. Original dumps and failed trials remain retained, and the Ubuntu-host crash remains unresolved.

## Scope

LocalStack is an emulator. These results do not establish live AWS throughput or latency, and no AWS account was contacted. Live mode remains explicitly selectable with the SDK credential chain and the same configuration for both contenders. Redis Streams is measured against Foundatio's preserved implementation; there is no MassTransit Redis transport in this comparison.

## Validation and retained evidence

- Core: 2,036 passed / 12 skipped. AWS: 63 passed / 8 skipped. Redis: 56 passed / 4 skipped. Measurement: 16 passed. **2,171 passed, 24 expected skips, zero regression-test failures.** All suites used the official runtime. The new acknowledgement-capacity regression failed before the change and passed afterward; the full AWS suite was rerun after the final AWS change.
- `Foundatio.slnx` Release build passed, with only the pre-existing ASPIRE010 warning. The aggregate `Foundatio.All.slnx` could not build in the isolated clone because its external sibling repositories are absent. The repository solution includes the temporary AWS and Redis providers and their tests.
- Targeted whitespace formatting, `git diff --check`, documentation build and benchmark summary regressions passed. The summary tests reject mixed runtime binaries even when the displayed runtime versions match.
- Regression coverage includes full receive-batch dispatch, overlapping receives under one capacity bound, bounded cleanup with blocked cancellation callbacks, sibling cancellation before reprovisioning, shared-batch cancellation, timeout/disposal behavior, per-entry acknowledgement validation, legacy/malformed envelope decoding and native-header validation. A LocalStack SNS tenant-filter test verifies selected native headers.
- The final resource inventory found no benchmark SQS queues, SNS topics or Redis keys. Conformance tests left their own 84 queues and six topics in the disposable broker; these were removed with the task-owned containers. Other development services were not modified.
- The final record audit verified all 93 unique resource prefixes, full publishing windows, bounded sampled outstanding counts, acknowledgement counts and histogram totals. Every admitted delivery drained. No crash dump was generated; temporary crash settings applied only to the completed Redis workers.
- [Raw results, manifests, scripts and summaries](baselines/2026-09-07-pipelines/) are retained. Earlier candidate and Ubuntu-runtime experiments are preserved separately in the local handoff; they are not merged into final medians. The diagnostic trace uses sampled thread time and includes waits; it is not a CPU-only hotspot ranking.

Changes are committed locally and are not published to PR #533. No hosted CI result is claimed for the unpublished commits.
