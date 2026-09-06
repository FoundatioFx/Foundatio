# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. These are local measurements, not cloud sizing claims.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 97506.9 (97506.9-97506.9) | 390027.7 | 0.051 / 10.623 / 11.647 | 29610.6 | 0.2186 |
| foundatio/memory queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 261223.3 (261223.3-261223.3) | 261223.3 | 3.935 / 4.799 / 5.503 | 12474.1 | 0.0375 |
| foundatio/redis pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 7947.3 (7947.3-7947.3) | 31789.3 | 23.807 / 137.215 / 169.983 | 59900.8 | 0.3115 |
| foundatio/redis queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 21301.8 (21301.8-21301.8) | 21301.8 | 47.103 / 51.711 / 88.063 | 21729.3 | 0.1359 |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 98.1 (98.1-98.1) | 392.5 | 10223.615 / 11272.191 / 11403.263 | 377580.8 | 2.169 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 753.2 (753.2-753.2) | 753.2 | 1294.335 / 1638.399 / 1703.935 | 119171.3 | 0.6601 |
| masstransit/memory pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 75201.9 (75201.9-75201.9) | 300807.5 | 6.143 / 16.255 / 22.015 | 65944.2 | 0.1202 |
| masstransit/memory queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 100338 (100338-100338) | 100338 | 10.239 / 11.135 / 14.591 | 22656.6 | 0.0364 |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 445.7 (445.7-445.7) | 1782.9 | 2260.991 / 2654.207 / 2818.047 | 189564.7 | 1.3981 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 2686.4 (2686.4-2686.4) | 2686.4 | 368.639 / 413.695 / 704.511 | 67429.9 | 0.3549 |

Failed trials: 1.
- round1-foundatio-redis-pubsub-four.json: Worker exited without a result. RUN fperf-fd29ab56ca9c foundatio/redis/pubsub
PHASE warmup 5s
Fatal error.
Internal CLR error. (0x80131506)
[createdump] Gathering state for process 3077623 dotnet
[createdump] Crashing thread 2ef630 signal 6 (0006)
[createdump] Writing minidump to file /tmp/foundatio-perf-dumps/dotnet_3077623_1788731298.dmp
[createdump] Written 335319040 bytes (81865 pages) to core file
[createdump] Target process is alive
[createdump] Dump successfully written in 283ms
