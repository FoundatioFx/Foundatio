# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. These are local measurements, not cloud sizing claims.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub p32 c8 s4 1024B b1 r50000 w1024 pf8 | 1 | 49998.3 (49998.3-49998.3) | 199993 | 0.903 / 2.303 / 2.847 | 27472.1 | 0.2324 |
| foundatio/memory queue p32 c32 s1 1024B b1 r50000 w1024 pf32 | 1 | 49998.9 (49998.9-49998.9) | 49998.9 | 0.687 / 1.327 / 1.631 | 8816.1 | 0.0487 |
| foundatio/redis pubsub p32 c8 s4 1024B b1 r10 w1024 pf8 | 1 | 10 (10-10) | 40 | 27.391 / 102.399 / 103.423 | 91185.1 | 6.3834 |
| foundatio/redis pubsub p32 c8 s4 1024B b1 r3000 w1024 pf8 | 1 | 2997.9 (2997.9-2997.9) | 11991.6 | 14.207 / 25.599 / 27.391 | 37761.6 | 0.4277 |
| foundatio/redis queue p32 c32 s1 1024B b1 r10 w1024 pf32 | 1 | 10 (10-10) | 10 | 26.623 / 101.375 / 103.423 | 36318.5 | 4.9785 |
| foundatio/redis queue p32 c32 s1 1024B b1 r3000 w1024 pf32 | 1 | 2997.9 (2997.9-2997.9) | 2997.9 | 14.207 / 25.855 / 27.135 | 17162.2 | 0.1953 |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r10 w1024 pf8 | 1 | 10 (10-10) | 40 | 23.039 / 27.135 / 28.415 | 339866.6 | 8.3583 |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r100 w1024 pf8 | 1 | 88.2 (88.2-88.2) | 353 | 2949.119 / 4161.535 / 4325.375 | 382118.1 | 3.0003 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r10 w1024 pf32 | 1 | 10 (10-10) | 10 | 7.423 / 9.599 / 10.623 | 173318.9 | 4.5851 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r100 w1024 pf32 | 1 | 100 (100-100) | 100 | 4.863 / 7.359 / 8.959 | 105989 | 1.9435 |
| masstransit/memory pubsub p32 c8 s4 1024B b1 r50000 w1024 pf8 | 1 | 49990.1 (49990.1-49990.1) | 199960.4 | 0.911 / 2.079 / 6.463 | 60968.3 | 0.0786 |
| masstransit/memory queue p32 c32 s1 1024B b1 r50000 w1024 pf32 | 1 | 49988.9 (49988.9-49988.9) | 49988.9 | 0.887 / 1.887 / 2.239 | 16154.9 | 0.0274 |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r10 w1024 pf8 | 1 | 10 (10-10) | 40 | 23.295 / 27.135 / 30.975 | 236999.9 | 11.4343 |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r100 w1024 pf8 | 1 | 99.9 (99.9-99.9) | 399.7 | 59.391 / 303.103 / 430.079 | 259311.4 | 4.7302 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r10 w1024 pf32 | 1 | 10 (10-10) | 10 | 9.727 / 11.135 / 11.775 | 193389.8 | 7.6765 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r100 w1024 pf32 | 1 | 100 (100-100) | 100 | 6.399 / 8.575 / 9.727 | 174643.3 | 2.4587 |

Failed trials: 0.
