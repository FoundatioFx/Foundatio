# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. These are local measurements, not cloud sizing claims.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| loopback/memory pubsub p32 c32 s4 1024B b1 r0 w1024 pf32 | 1 | 1242225.8 (1242225.8-1242225.8) | 4968903.4 | 0 / 0.003 / 0.004 | 80 | 0.0064 |
| loopback/memory queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 1466421 (1466421-1466421) | 1466421 | 0 / 0 / 0 | 80 | 0.0041 |

Failed trials: 0.
