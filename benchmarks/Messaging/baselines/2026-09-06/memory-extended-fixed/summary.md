# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. These are local measurements, not cloud sizing claims.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub p32 c8 s4 16384B b1 r0 w1024 pf8 | 1 | 59583.1 (59583.1-59583.1) | 238332.3 | 4.351 / 20.479 / 27.135 | 230057.7 | 0.2621 |
| foundatio/memory pubsub p8 c8 s4 1024B b10 r0 w1024 pf8 | 1 | 98545.2 (98545.2-98545.2) | 394181 | 0.245 / 10.495 / 11.519 | 28552.5 | 0.2099 |
| foundatio/memory queue p32 c32 s1 16384B b1 r0 w1024 pf32 | 1 | 79280.6 (79280.6-79280.6) | 79280.6 | 11.903 / 18.943 / 24.575 | 122308.7 | 0.1403 |
| foundatio/memory queue p8 c32 s1 1024B b10 r0 w1024 pf32 | 1 | 282546 (282546-282546) | 282546 | 3.487 / 4.287 / 4.927 | 11707.5 | 0.0376 |
| masstransit/memory pubsub p32 c8 s4 16384B b1 r0 w1024 pf8 | 1 | 39049 (39049-39049) | 156195.9 | 13.951 / 30.975 / 39.423 | 327064.6 | 0.2658 |
| masstransit/memory pubsub p8 c8 s4 1024B b10 r0 w1024 pf8 | 1 | 76375.4 (76375.4-76375.4) | 305501.7 | 6.335 / 15.231 / 19.711 | 65826.1 | 0.116 |
| masstransit/memory queue p32 c32 s1 16384B b1 r0 w1024 pf32 | 1 | 48865.7 (48865.7-48865.7) | 48865.7 | 19.711 / 27.647 / 35.839 | 99457.2 | 0.1529 |
| masstransit/memory queue p8 c32 s1 1024B b10 r0 w1024 pf32 | 1 | 98847.4 (98847.4-98847.4) | 98847.4 | 9.983 / 11.263 / 14.207 | 22539.3 | 0.0365 |

Failed trials: 0.
