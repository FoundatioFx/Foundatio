# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 324.5 (324.5-324.5) | 1298 | 2490.367 / 3702.783 / 3768.319 | 192605.6 | 2.0424 |
| foundatio/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 1 | 348.5 (348.5-348.5) | 348.5 | 2064.383 / 2228.223 / 2241.054 | 89479.1 | 2.2046 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 2088.4 (2088.4-2088.4) | 2088.4 | 458.751 / 835.583 / 843.775 | 39907.7 | 0.5378 |

Failed trials: 0.
