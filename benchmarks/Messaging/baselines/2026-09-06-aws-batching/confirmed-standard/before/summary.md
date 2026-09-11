# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 323.9 (294.5-330.4) | 323.9 | 3014.655 / 4095.999 / 4194.303 | 148867.8 | 1.1149 |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 97.3 (97-104.5) | 389.1 | 9043.967 / 10354.687 / 10542.037 | 377167.7 | 2.3486 |
| foundatio/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 397.9 (383-414) | 397.9 | 2326.527 / 2719.743 / 2750.253 | 131190.8 | 0.8663 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 736.4 (720-744.4) | 736.4 | 1310.719 / 1654.783 / 1668.594 | 109021.7 | 0.756 |

Failed trials: 0.
