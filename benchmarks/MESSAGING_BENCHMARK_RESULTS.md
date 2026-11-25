# Messaging and durable job benchmark results

v2 messaging and durable jobs on the in-memory transport and job store, against the legacy `InMemoryQueue<T>` worked by
`QueueJobBase<T>` loops, the legacy `InMemoryMessageBus`, and a floor: a channel of System.Text.Json bodies read by as
many loops as the handler concurrency.

Run with:

```bash
dotnet run -c Release --project benchmarks/Foundatio.Benchmarks.csproj -- --filter '*Messaging*' '*DurableJob*'
```

Measured on an Ubuntu box with .NET 10.0.115, pinned to 6 cores (`taskset -c 0-5`), with BenchmarkDotNet's in-process
toolchain, 3 warmup and 8 measured iterations. Each benchmark method ran in its own process. Times are per message (or
job), as nanoseconds, with about ±10% noise. Bytes are allocated per message on all threads. "v2 before" is the code before the
round 7 performance fixes; the legacy and floor columns are after them, and the serializer fix also benefits the
legacy bus. The legacy queue never serializes: it deep-clones the payload, so its 16 KB rows don't pay for the body.

## End to end: send to handled (ns and bytes per message), after the fixes

| Case | v2 before | v2 after | legacy queue job | legacy bus | floor | v2 / legacy queue | v2 / floor |
|---|---|---|---|---|---|---|---|
| throughput c=1 100 B | 3,850 ns / 4,598 B | **2,785 ns / 2,560 B** | 1,333 / 3,574 | 1,375 / 2,540 | 157 / 344 | 2.09x / 0.72x | 17.7x / 7.4x |
| throughput c=1 16384 B | 13,750 ns / 102,369 B | **9,208 ns / 51,415 B** | 1,330 / 3,574 | 3,482 / 51,395 | 5,458 / 49,196 | 6.92x / 14.39x | 1.7x / 1.0x |
| throughput c=8 100 B | 2,096 ns / 4,096 B | **1,349 ns / 2,048 B** | 1,011 / 4,649 | 462 / 2,540 | 674 / 548 | 1.33x / 0.44x | 2.0x / 3.7x |
| throughput c=8 16384 B | 10,373 ns / 102,021 B | **6,369 ns / 51,077 B** | 1,023 / 4,649 | 1,720 / 51,395 | 2,160 / 49,743 | 6.23x / 10.99x | 2.9x / 1.0x |
| throughput c=64 100 B | 2,072 ns / 4,076 B | **1,385 ns / 2,048 B** | 1,089 / 4,649 | 386 / 2,550 | 883 / 808 | 1.27x / 0.44x | 1.6x / 2.5x |
| throughput c=64 16384 B | 10,525 ns / 102,001 B | **6,532 ns / 51,036 B** | 1,087 / 4,639 | 1,552 / 51,395 | 2,826 / 50,519 | 6.01x / 11.00x | 2.3x / 1.0x |
| latency c=1 100 B | 3,598 ns / 6,431 B | **3,406 ns / 4,536 B** | 2,406 / 4,987 | 1,465 / 2,744 | 1,042 / 536 | 1.42x / 0.91x | 3.3x / 8.5x |
| latency c=1 16384 B | 6,967 ns / 104,212 B | **5,516 ns / 53,391 B** | 2,386 / 4,987 | 3,599 / 51,589 | 3,346 / 49,384 | 2.31x / 10.71x | 1.6x / 1.1x |
| latency c=8 100 B | 4,220 ns / 6,717 B | **3,581 ns / 4,895 B** | 2,471 / 5,028 | 1,537 / 2,744 | 1,557 / 1,041 | 1.45x / 0.97x | 2.3x / 4.7x |
| latency c=8 16384 B | 7,188 ns / 104,489 B | **5,659 ns / 53,760 B** | 2,488 / 5,028 | 3,694 / 51,589 | 3,823 / 50,055 | 2.27x / 10.69x | 1.5x / 1.1x |
| latency c=64 100 B | 4,374 ns / 6,707 B | **3,704 ns / 4,874 B** | 2,550 / 5,028 | 1,548 / 2,744 | 2,485 / 1,980 | 1.45x / 0.97x | 1.5x / 2.5x |
| latency c=64 16384 B | 7,270 ns / 104,489 B | **5,636 ns / 53,760 B** | 2,539 / 5,028 | 3,689 / 51,589 | 6,360 / 52,957 | 2.22x / 10.69x | 0.9x / 1.0x |

## Send only, fan-out, failure paths, jobs

| Case | v2 before | v2 after | legacy | floor |
|---|---|---|---|---|
| send 100 B (legacy: bus publish incl. delivery) | 904 ns / 2,478 B | **685 ns / 1,556 B** | 1,407 / 2,540 | 108 / 128 |
| send 16384 B (legacy: bus publish incl. delivery) | 8,568 ns / 67,697 B | **2,394 ns / 17,838 B** | 3,583 / 51,395 | 1,333 / 16,408 |
| publish to 1 subscription(s), c=8 | 1,756 / 3,983 | **1,122 / 2,048** | 465 / 2,550 | |
| publish to 5 subscription(s), c=8 | 5,665 / 17,551 | **4,222 / 10,844** | 851 / 5,786 | |
| success, c=8 | 1,257 / 4,137 | **994 / 2,028** | 1,068 / 4,649 | |
| retry once (throw) | 2,710 / 8,223 | **2,333 / 5,028** | 1,002 / 6,625 | |
| retry once (context.Retry) | 2,021 / 7,117 | **1,694 / 3,881** | 1,002 / 6,625 | |
| dead-letter (throw, no retries) | 2,653 / 6,595 | **2,073 / 4,321** | 1,130 / 4,936 | |
| dead-letter (context.DeadLetter) | 1,409 / 5,284 | **1,104 / 2,980** | 1,130 / 4,936 | |
| job enqueue | 1,200 / 2,028 | **870 / 1,280** | 550 / 1,106 | |
| job enqueue to run, c=8 | 5,537 / 7,690 | **3,761 / 5,356** | 1,063 / 4,649 | |
| job enqueue to run, one at a time | 7,234 / 9,513 | **5,723 / 6,953** | 2,417 / 5,028 | |

## Azure Storage Queues on Azurite (per-broker harness)

`benchmarks/Foundatio.Messaging.Benchmarks --transport azurestorage`, client pinned to 8 cores (`taskset -c 0-7`), against
Azurite 3.37.0 pinned to 6 others and shared with other test runs. 100-byte bodies, 2,000 messages per run, three
interleaved runs per row. Azurite serves requests on one thread and was at 100% CPU in every consume run, so throughput
is Azurite's ceiling (and drifts between 250 and 650 messages/s with its load), not the transport's: compare the rows
of a run, not the absolute numbers. Real Azure targets about 2,000 messages/s per queue.

| Drain a backlog (messages/s) | c=1 | c=16 | Client CPU per message | Allocated per message | Storage calls per message |
|---|---|---|---|---|---|
| v2 (`UseAzureStorageQueues`) | 478, 599, 553 | 623, 496, 408 | 0.9–1.4 ms | 24 KB | 1.03 (a delete each, a receive per ~30) |
| Raw SDK (receives of 32, concurrent deletes) | 434, 600, 633 | 496, 586, 440 | 0.7–1.4 ms | 20 KB | 1.06 |
| Legacy `AzureStorageQueue<T>` (`DequeueInterval` 100 ms) | 234, 243, 160 | 245, 231, 282 | 1.1–1.3 ms | 40 KB | 2.0 (a receive per message) |

Most of the client CPU is the Azure SDK's HTTP pipeline (request signing, XML), which the raw target pays too.

| Latency and idle cost (v2, c=16) | Result |
|---|---|
| Send → handled at 100 messages/s | p50 29 ms, p95 53 ms, p99 55 ms; 0.43 receives per message |
| Send → handled at 10 messages/s | p50 56 ms, p95 104 ms |
| First message right after start | 97 ms |
| First message after 30 s idle, sent from another host | 2.06 s (`MaxPollInterval`) |
| Idle receiver | 17 receives in 30 s (one poller, every 2 s) |
| Publish to a queue, 16 publishers | 1,702 messages/s, one `Put Message` each |
| Publish to a topic with one subscription, 16 publishers | 1,536 messages/s, plus one `List Queues` per publish |

The raw SDK's latency floor is p50 4.5 ms at 100 messages/s, polling 530 times a second; v2 polls 43 times a second
there. `MinPollInterval` trades the two: 25 ms gave p50 16 ms with 1,496 receives in 20 s, 50 ms (the default) p50 29 ms
with 861, and 100 ms p50 53 ms with 458.
