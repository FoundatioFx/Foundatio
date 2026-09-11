# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r10 w1024 pf8 | 1 | 10 (10-10) | 40 | 23.295 / 27.647 / 29.183 | 573996.6 | 12.2931 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r10 w1024 pf32 | 1 | 10 (10-10) | 10 | 8.959 / 10.879 / 11.647 | 193991.8 | 7.4977 |

Failed trials: 0.
