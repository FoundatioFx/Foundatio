# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r100 w1024 pf8 | 1 | 99.8 (99.8-99.8) | 399.1 | 55.295 / 274.431 / 438.271 | 396204.2 | 4.0287 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r100 w1024 pf32 | 1 | 100 (100-100) | 100 | 5.055 / 6.975 / 7.999 | 55241.7 | 2.3713 |

Failed trials: 0.
