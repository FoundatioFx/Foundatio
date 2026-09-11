# Messaging benchmark results

Medians across successful fresh-process trials; ranges are observed throughput variation. Latency includes broker acknowledgement. Fanout deliveries/s counts each subscriber copy. Allocation/CPU include the client and measurement harness, and exclude broker processes. These are local measurements, not cloud sizing claims.

| Case | Trials | Inputs/s (min-max) | Deliveries/s | p50 / p95 / p99 ms | Bytes/input | CPU ms/input |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| foundatio/memory pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 210216.2 (206762-213550.2) | 210216.2 | 4.287 / 8.703 / 16.127 | 13189.8 | 0.0427 |
| foundatio/memory pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 2 | 87561.1 (85996.3-89125.9) | 350244.4 | 0.082 / 10.687 / 23.167 | 30732.4 | 0.2351 |
| foundatio/memory queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 110286.8 (108516.2-117264.2) | 110286.8 | 8.447 / 13.823 / 23.551 | 13486.6 | 0.0489 |
| foundatio/memory queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 213748.9 (213651.9-215298.2) | 213748.9 | 4.159 / 8.447 / 16.895 | 12793.2 | 0.0415 |
| foundatio/redis pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 21109.3 (20905.6-21208.7) | 21109.3 | 47.103 / 53.247 / 89.087 | 22867.9 | 0.134 |
| foundatio/redis pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 7974.8 (7907.6-8089.4) | 31899.3 | 106.495 / 139.263 / 159.743 | 62596.3 | 0.3257 |
| foundatio/redis queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 5539.9 (5530.2-5556.7) | 5539.9 | 184.319 / 200.703 / 229.375 | 17779.6 | 0.3379 |
| foundatio/redis queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 21233 (20886.3-21266.9) | 21233 | 47.103 / 53.247 / 88.063 | 22679.7 | 0.1344 |
| foundatio/sqs pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 325.7 (317.2-328.9) | 325.7 | 3047.423 / 3997.695 / 4095.999 | 74374.4 | 1.0478 |
| foundatio/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 100.8 (87.5-101) | 403.2 | 8912.895 / 9699.327 / 9961.471 | 378535.4 | 2.1723 |
| foundatio/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 406.2 (398.6-423.8) | 406.2 | 2195.455 / 2686.975 / 2752.511 | 127522.5 | 0.8973 |
| foundatio/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 715.1 (713.2-720.9) | 715.1 | 1294.335 / 1769.471 / 1802.239 | 21007.1 | 0.7896 |
| masstransit/memory pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 100055 (97742.3-102936.6) | 100055 | 10.111 / 13.183 / 15.231 | 23135 | 0.0361 |
| masstransit/memory pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 73712.5 (70149.2-73771.4) | 294850.2 | 5.759 / 16.255 / 21.503 | 65940.2 | 0.1196 |
| masstransit/memory queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 124599.5 (124385.9-126402.6) | 124599.5 | 8.319 / 9.087 / 11.391 | 19676.4 | 0.029 |
| masstransit/memory queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 101358.1 (100398.3-102476.1) | 101358.1 | 10.239 / 10.879 / 14.079 | 22655.3 | 0.036 |
| masstransit/sqs pubsub p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 1309.5 (1253.5-1338.1) | 1309.5 | 745.471 / 1081.343 / 1130.495 | 60884.7 | 0.7053 |
| masstransit/sqs pubsub p32 c8 s4 1024B b1 r0 w1024 pf8 | 3 | 420.1 (404.6-420.5) | 1680.4 | 2260.991 / 3178.495 / 3375.103 | 84800.1 | 1.746 |
| masstransit/sqs queue p1 c1 s1 1024B b1 r0 w1024 pf1 | 3 | 282.4 (278.3-288.9) | 282.4 | 1900.543 / 3080.191 / 3145.727 | 162523.4 | 1.666 |
| masstransit/sqs queue p32 c32 s1 1024B b1 r0 w1024 pf32 | 3 | 2637.1 (2630-2678.4) | 2637.1 | 372.735 / 438.271 / 712.703 | 67436.5 | 0.4545 |

Failed trials: 1.
- round2-foundatio-memory-pubsub-four.json: Worker exited without a result. RUN fperf-6b4027ac3eba foundatio/memory/pubsub
Fatal error.
Internal CLR error. (0x80131506)
[createdump] Gathering state for process 2967013 dotnet
[createdump] Crashing thread 2d461f signal 6 (0006)
[createdump] Writing minidump to file /tmp/foundatio-perf-dumps/dotnet_2967013_1788728719.dmp
[createdump] Written 233418752 bytes (56987 pages) to core file
[createdump] Target process is alive
[createdump] Dump successfully written in 234ms
