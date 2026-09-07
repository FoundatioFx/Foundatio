# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 314.6 (314.6-314.6) | 1258.2 | 3211.263 / 3801.087 / 3899.391 | 196403.7 | 2.1928 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 2201.2 (2201.2-2201.2) | 2201.2 | 454.655 / 516.095 / 802.815 | 59637.3 | 0.459 |

Failed trials: 0.
