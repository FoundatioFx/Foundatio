# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 434 (434-434) | 1736 | 2326.527 / 2719.743 / 2916.351 | 193997.3 | 1.3269 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 2652.4 (2652.4-2652.4) | 2652.4 | 372.735 / 475.135 / 712.703 | 67426.2 | 0.3613 |

Failed trials: 0.
