# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| masstransit/sqs pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 1271 (1218.2-1300.1) | 1271 | 811.007 / 1064.959 / 1130.495 | 81061.2 | 0.7045 |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 420.4 (416.1-425.1) | 1681.6 | 2195.455 / 2883.583 / 3014.655 | 106786.8 | 1.6302 |
| masstransit/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 292.8 (273.9-298) | 292.8 | 1949.695 / 3047.423 / 3056.547 | 165490.4 | 1.8646 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 2613.7 (2608.2-2616.9) | 2613.7 | 380.927 / 421.887 / 696.319 | 67433.1 | 0.4315 |

Failed trials: 0.
