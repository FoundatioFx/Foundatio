# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. Results describe this client and broker configuration; LocalStack results do not predict AWS service performance.

AWS target: SQS/SNS custom endpoint; mode: localstack; region: us-east-1.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/sqs pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 314.8 (256.2-341.9) | 314.8 | 3276.799 / 4128.767 / 4194.303 | 81818 | 1.0594 |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 99.3 (88.9-100.6) | 397.1 | 8912.895 / 9830.399 / 10092.543 | 377842.6 | 2.3404 |
| foundatio/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 377.2 (376.8-402.1) | 377.2 | 2260.991 / 2818.047 / 2829.273 | 127042.1 | 0.8903 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 712.8 (703.4-732.2) | 712.8 | 1327.103 / 1703.935 / 1749.397 | 109019.1 | 0.7151 |

Failed trials: 0.
