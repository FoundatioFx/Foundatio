# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r100 w1024 pf8 | 1 | 99.6 (99.6-99.6) | 398.5 | 88.063 / 335.871 / 479.231 | 289505.3 | 4.1795 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r100 w1024 pf32 | 1 | 100 (100-100) | 100 | 6.335 / 7.999 / 8.959 | 175012.5 | 2.7409 |

Failed trials: 0.
