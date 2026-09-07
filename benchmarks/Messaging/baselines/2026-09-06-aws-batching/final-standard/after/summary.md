# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 1215.7 (1135.6-1225.6) | 1215.7 | 786.431 / 1146.879 / 1163.263 | 28470.1 | 0.4833 |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 302.5 (289.9-327.2) | 1210 | 3276.799 / 4030.463 / 4259.839 | 144762.1 | 1.4238 |
| foundatio/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 277.2 (277.1-284.2) | 277.2 | 3473.407 / 3735.551 / 3745.342 | 115279.7 | 1.2093 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 2327.8 (2284-2382.3) | 2327.8 | 421.887 / 471.039 / 753.663 | 59274.6 | 0.3183 |

Failed trials: 0.
