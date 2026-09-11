# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 404.1 (404.1-404.1) | 1616.3 | 2080.767 / 3112.959 / 3211.263 | 205907.6 | 2.1802 |
| masstransit/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 1 | 269.2 (269.2-269.2) | 269.2 | 1081.343 / 1671.167 / 1700.976 | 186254.6 | 3.1554 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 2456.5 (2456.5-2456.5) | 2456.5 | 385.023 / 712.703 / 720.895 | 67620.8 | 0.8177 |

Failed trials: 0.
