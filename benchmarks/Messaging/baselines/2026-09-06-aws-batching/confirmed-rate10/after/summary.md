# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r10 w1024 pf8 | 1 | 10 (10-10) | 40.1 | 22.015 / 26.367 / 27.647 | 377463 | 9.8974 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r10 w1024 pf32 | 1 | 10 (10-10) | 10 | 7.743 / 9.471 / 9.983 | 181390.1 | 5.7204 |

Failed trials: 0.
