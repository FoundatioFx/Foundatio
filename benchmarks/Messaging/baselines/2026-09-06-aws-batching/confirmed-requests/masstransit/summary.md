# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 1 | 440 (440-440) | 1760 | 2228.223 / 2818.047 / 3047.423 | 146111.1 | 2.509 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 1 | 2649.2 (2649.2-2649.2) | 2649.2 | 372.735 / 413.695 / 696.319 | 45288.3 | 0.6408 |

Failed trials: 0.
