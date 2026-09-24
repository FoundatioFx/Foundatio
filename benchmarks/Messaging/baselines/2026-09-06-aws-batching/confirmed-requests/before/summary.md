# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 87.2 (87.2-87.2) | 349 | 9437.183 / 11272.191 / 11403.263 | 378277.2 | 4.2552 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 627.7 (627.7-627.7) | 627.7 | 1458.175 / 2097.151 / 2129.919 | 109404.4 | 1.2813 |

Failed trials: 0.
