# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. These are local measurements, not cloud sizing claims.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 246508.4 (245755.4-247629.1) | 246508.4 | 4.159 / 4.991 / 5.759 | 12868.3 | 0.038 |
| foundatio/memory pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 95854.8 (95518.4-96101.8) | 383419 | 0.082 / 10.879 / 12.031 | 29604.8 | 0.2213 |
| foundatio/memory queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 112276.9 (108152.1-124646.2) | 112276.9 | 9.343 / 11.135 / 11.775 | 13111.9 | 0.0434 |
| foundatio/memory queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 258086.7 (252717-264568.7) | 258086.7 | 3.967 / 4.799 / 5.439 | 12474.2 | 0.0372 |
| masstransit/memory pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 102242.6 (99263.1-102515.9) | 102242.6 | 9.983 / 11.135 / 15.359 | 23135.7 | 0.0359 |
| masstransit/memory pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 73940.2 (72783.2-76190.7) | 295760.8 | 5.567 / 16.383 / 21.503 | 65939.4 | 0.1129 |
| masstransit/memory queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 122978.3 (122442.1-123201.7) | 122978.3 | 8.447 / 10.239 / 12.543 | 19675.5 | 0.0293 |
| masstransit/memory queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 100661 (100145.9-101434.6) | 100661 | 10.239 / 11.263 / 14.463 | 22655 | 0.036 |

Failed trials: 0.
