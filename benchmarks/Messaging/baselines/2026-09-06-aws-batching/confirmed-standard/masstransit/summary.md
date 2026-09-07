# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| masstransit/sqs pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 1293.3 (1244.8-1353.2) | 1293.3 | 770.047 / 983.039 / 1040.383 | 80948.4 | 0.6499 |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 439.1 (421-467.5) | 1756.5 | 2195.455 / 2981.887 / 3145.727 | 84765.4 | 1.7356 |
| masstransit/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 293.9 (247.4-299.2) | 293.9 | 1835.007 / 3178.495 / 3203.502 | 185334.4 | 1.8633 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 2584.3 (2514.9-2587.2) | 2584.3 | 380.927 / 442.367 / 729.087 | 67453.6 | 0.4702 |

Failed trials: 0.
