```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26100.4484/24H2/2024Update/HudsonValley)
Unknown processor
.NET SDK 9.0.301
  [Host]     : .NET 8.0.17 (8.0.1725.26602), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.17 (8.0.1725.26602), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun   : .NET 8.0.17 (8.0.1725.26602), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                                           | Job        | IterationCount | LaunchCount | WarmupCount | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------------- |----------- |--------------- |------------ |------------ |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| DirectCall_Async                                 | DefaultJob | Default        | Default     | Default     |   465.54 ns |   2.769 ns |  2.590 ns |  1.00 |    0.01 | 0.0038 |     213 B |        1.00 |
| ResiliencePolicy_NoRetries_Async                 | DefaultJob | Default        | Default     | Default     |   598.39 ns |   6.873 ns |  6.429 ns |  1.29 |    0.02 | 0.0153 |     810 B |        3.80 |
| ResiliencePolicy_StandardConfig_Async            | DefaultJob | Default        | Default     | Default     |   598.80 ns |   8.342 ns |  7.803 ns |  1.29 |    0.02 | 0.0153 |     811 B |        3.81 |
| ResiliencePolicy_NoRetries_WithResult_Async      | DefaultJob | Default        | Default     | Default     |    39.33 ns |   0.459 ns |  0.430 ns |  0.08 |    0.00 | 0.0027 |     136 B |        0.64 |
| ResiliencePolicy_StandardConfig_WithResult_Async | DefaultJob | Default        | Default     | Default     |    39.39 ns |   0.308 ns |  0.288 ns |  0.08 |    0.00 | 0.0027 |     136 B |        0.64 |
| DirectCall_ComputeIntensive_Async                | DefaultJob | Default        | Default     | Default     |   690.91 ns |   3.900 ns |  3.457 ns |  1.48 |    0.01 | 0.0019 |      96 B |        0.45 |
| ResiliencePolicy_ComputeIntensive_Async          | DefaultJob | Default        | Default     | Default     |   817.21 ns |   5.533 ns |  5.176 ns |  1.76 |    0.01 | 0.0143 |     744 B |        3.49 |
| Polly_NoRetries_Async                            | DefaultJob | Default        | Default     | Default     |   607.56 ns |   3.486 ns |  2.911 ns |  1.31 |    0.01 | 0.0134 |     686 B |        3.22 |
| Polly_StandardConfig_Async                       | DefaultJob | Default        | Default     | Default     | 1,089.80 ns |  21.529 ns | 20.138 ns |  2.34 |    0.04 | 0.0267 |    1403 B |        6.59 |
| Polly_NoRetries_WithResult_Async                 | DefaultJob | Default        | Default     | Default     |    38.99 ns |   0.358 ns |  0.335 ns |  0.08 |    0.00 | 0.0027 |     136 B |        0.64 |
| Polly_StandardConfig_WithResult_Async            | DefaultJob | Default        | Default     | Default     |   133.31 ns |   0.832 ns |  0.778 ns |  0.29 |    0.00 | 0.0026 |     136 B |        0.64 |
| Polly_ComputeIntensive_Async                     | DefaultJob | Default        | Default     | Default     |   794.89 ns |  15.182 ns | 16.874 ns |  1.71 |    0.04 | 0.0095 |     504 B |        2.37 |
|                                                  |            |                |             |             |             |            |           |       |         |        |           |             |
| DirectCall_Async                                 | ShortRun   | 3              | 1           | 3           |   465.76 ns |  49.974 ns |  2.739 ns |  1.00 |    0.01 | 0.0038 |     214 B |        1.00 |
| ResiliencePolicy_NoRetries_Async                 | ShortRun   | 3              | 1           | 3           |   585.32 ns | 295.073 ns | 16.174 ns |  1.26 |    0.03 | 0.0153 |     812 B |        3.79 |
| ResiliencePolicy_StandardConfig_Async            | ShortRun   | 3              | 1           | 3           |   595.69 ns | 114.281 ns |  6.264 ns |  1.28 |    0.01 | 0.0153 |     810 B |        3.79 |
| ResiliencePolicy_NoRetries_WithResult_Async      | ShortRun   | 3              | 1           | 3           |    39.96 ns |   4.972 ns |  0.273 ns |  0.09 |    0.00 | 0.0027 |     136 B |        0.64 |
| ResiliencePolicy_StandardConfig_WithResult_Async | ShortRun   | 3              | 1           | 3           |    39.95 ns |   7.586 ns |  0.416 ns |  0.09 |    0.00 | 0.0027 |     136 B |        0.64 |
| DirectCall_ComputeIntensive_Async                | ShortRun   | 3              | 1           | 3           |   689.48 ns |  28.672 ns |  1.572 ns |  1.48 |    0.01 | 0.0019 |      96 B |        0.45 |
| ResiliencePolicy_ComputeIntensive_Async          | ShortRun   | 3              | 1           | 3           |   815.53 ns | 164.814 ns |  9.034 ns |  1.75 |    0.02 | 0.0143 |     744 B |        3.48 |
| Polly_NoRetries_Async                            | ShortRun   | 3              | 1           | 3           |   598.62 ns |  55.207 ns |  3.026 ns |  1.29 |    0.01 | 0.0134 |     685 B |        3.20 |
| Polly_StandardConfig_Async                       | ShortRun   | 3              | 1           | 3           | 1,097.78 ns | 222.014 ns | 12.169 ns |  2.36 |    0.03 | 0.0267 |    1414 B |        6.61 |
| Polly_NoRetries_WithResult_Async                 | ShortRun   | 3              | 1           | 3           |    40.97 ns |   5.402 ns |  0.296 ns |  0.09 |    0.00 | 0.0027 |     136 B |        0.64 |
| Polly_StandardConfig_WithResult_Async            | ShortRun   | 3              | 1           | 3           |   131.86 ns |   6.455 ns |  0.354 ns |  0.28 |    0.00 | 0.0026 |     136 B |        0.64 |
| Polly_ComputeIntensive_Async                     | ShortRun   | 3              | 1           | 3           |   794.31 ns | 105.834 ns |  5.801 ns |  1.71 |    0.01 | 0.0095 |     504 B |        2.36 |
