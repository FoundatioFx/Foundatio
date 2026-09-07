# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 303.8 (303.8-303.8) | 1215.1 | 3309.567 / 3932.159 / 4194.303 | 187601.6 | 1.1242 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 2438.9 (2438.9-2438.9) | 2438.9 | 405.503 / 466.943 / 737.279 | 32788.3 | 0.2627 |

Failed trials: 0.
