# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. These are local measurements, not cloud sizing claims.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub p32 c8 s4 16384B b1 r0 w1024 pf8 | 1 | 58595.7 (58595.7-58595.7) | 234382.7 | 7.167 / 19.455 / 34.815 | 231527.8 | 0.2561 |
| foundatio/memory pubsub p8 c8 s4 1024B b10 r0 w1024 pf8 | 1 | 91830.1 (91830.1-91830.1) | 367320.2 | 0.263 / 10.111 / 21.503 | 29596.7 | 0.2181 |
| foundatio/memory queue p32 c32 s1 16384B b1 r0 w1024 pf32 | 1 | 90612.7 (90612.7-90612.7) | 90612.7 | 11.135 / 14.847 / 24.831 | 122615.7 | 0.0911 |
| foundatio/memory queue p8 c32 s1 1024B b10 r0 w1024 pf32 | 1 | 218399.9 (218399.9-218399.9) | 218399.9 | 3.935 / 6.079 / 17.919 | 12017.3 | 0.0399 |
| foundatio/redis pubsub p32 c8 s4 16384B b1 r0 w1024 pf8 | 1 | 3169.8 (3169.8-3169.8) | 12679.3 | 274.431 / 339.967 / 348.159 | 203552.4 | 0.5459 |
| foundatio/redis pubsub p8 c8 s4 1024B b10 r0 w1024 pf8 | 1 | 7286.3 (7286.3-7286.3) | 29145.3 | 123.903 / 145.407 / 167.935 | 61176.8 | 0.3234 |
| foundatio/redis queue p32 c32 s1 16384B b1 r0 w1024 pf32 | 1 | 7728.7 (7728.7-7728.7) | 7728.7 | 124.927 / 169.983 / 178.175 | 118620.4 | 0.205 |
| foundatio/redis queue p8 c32 s1 1024B b10 r0 w1024 pf32 | 1 | 20220.8 (20220.8-20220.8) | 20220.8 | 47.615 / 54.783 / 91.135 | 21248.3 | 0.1152 |
| foundatio/sqs pubsub p32 c8 s4 16384B b1 r0 w1024 pf8 | 1 | 91 (91-91) | 364.2 | 9175.039 / 10354.687 / 10747.903 | 953889.3 | 2.8806 |
| foundatio/sqs pubsub p8 c8 s4 1024B b10 r0 w1024 pf8 | 1 | 108.8 (108.8-108.8) | 435.2 | 7077.887 / 9437.183 / 9830.399 | 327364.5 | 1.95 |
| foundatio/sqs queue p32 c32 s1 16384B b1 r0 w1024 pf32 | 1 | 645.9 (645.9-645.9) | 645.9 | 1409.023 / 1851.391 / 1867.775 | 347501.6 | 0.9147 |
| foundatio/sqs queue p8 c32 s1 1024B b10 r0 w1024 pf32 | 1 | 1117.4 (1117.4-1117.4) | 1117.4 | 827.391 / 1261.567 / 1277.951 | 58017.9 | 0.4245 |
| masstransit/memory pubsub p32 c8 s4 16384B b1 r0 w1024 pf8 | 1 | 37892.9 (37892.9-37892.9) | 151571.7 | 15.743 / 33.279 / 40.959 | 327066.5 | 0.2917 |
| masstransit/memory pubsub p8 c8 s4 1024B b10 r0 w1024 pf8 | 1 | 77734.8 (77734.8-77734.8) | 310939.2 | 6.527 / 14.975 / 19.455 | 65825.4 | 0.1143 |
| masstransit/memory queue p32 c32 s1 16384B b1 r0 w1024 pf32 | 1 | 48503.7 (48503.7-48503.7) | 48503.7 | 19.711 / 28.927 / 33.791 | 99460.4 | 0.1667 |
| masstransit/memory queue p8 c32 s1 1024B b10 r0 w1024 pf32 | 1 | 99020.7 (99020.7-99020.7) | 99020.7 | 9.983 / 11.647 / 14.335 | 22539 | 0.0362 |
| masstransit/sqs pubsub p32 c8 s4 16384B b1 r0 w1024 pf8 | 1 | 303.1 (303.1-303.1) | 1212.6 | 3112.959 / 4587.519 / 4718.591 | 760327.9 | 2.6512 |
| masstransit/sqs pubsub p8 c8 s4 1024B b10 r0 w1024 pf8 | 1 | 499.6 (499.6-499.6) | 1998.3 | 1818.623 / 2523.135 / 2654.207 | 77792.4 | 1.474 |
| masstransit/sqs queue p32 c32 s1 16384B b1 r0 w1024 pf32 | 1 | 1261 (1261-1261) | 1261 | 778.239 / 843.775 / 860.159 | 240453.6 | 1.031 |
| masstransit/sqs queue p8 c32 s1 1024B b10 r0 w1024 pf32 | 1 | 2757.5 (2757.5-2757.5) | 2757.5 | 348.159 / 421.887 / 737.279 | 66182.5 | 0.4325 |

Failed trials: 0.
