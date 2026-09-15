# AWS batching measurements

See [the follow-up report](../../AWS_BATCHING_RESULTS.md) for conclusions and methodology. These results supplement the historical 141-trial baseline; they do not overwrite it.

| Profile | Trials | Implementation |
| --- | ---: | --- |
| confirmed-standard | 36 | Three interleaved repetitions of before / final / MassTransit across four workloads |
| confirmed-rate10 | 4 | Final / MassTransit, 30 seconds at 10 inputs/s |
| confirmed-rate100 | 4 | Final / MassTransit, 30 seconds at 100 inputs/s |
| confirmed-soak | 4 | Final / MassTransit, 120 seconds each, 20-million-input tracking capacity |
| confirmed-requests | 6 | Before / final / MassTransit, 10 seconds without warmup for broker request accounting |
| final-standard | 36 | Earlier fixed-delay candidate, retained under its original capture-directory name |
| adaptive-check | 6 | Five-second refinement check; exploratory |

All 96 workers returned success. The final conclusions use the 54 `confirmed-*` trials. The `after` variant in `final-standard` is commit `1a9f0e62`; `after` in the other profiles is `abce1c0e`. The before binary was preserved at checkout `5dae40ef`, with unchanged shipping assemblies built at `95983490`. Each profile's binary manifest retains the actual source revision and SHA-256 of every dependency.

`raw-trials.tar.gz` contains the individual JSON results, worker logs, native request counts, run options, binary manifests, per-revision summaries, and capture/validation scripts. The scripts record the original local paths; adapt those paths to prepared before/after executable directories when replaying elsewhere. The supported cross-platform entrypoint is the [PowerShell benchmark runner](../../README.md). Do not mix the earlier and final implementation summaries.

The validation script checks success, exact fanout counts, histogram count, absence of lost/duplicate/invalid deliveries, tracking bounds, throughput arithmetic, and publishing duration with one-millisecond timer tolerance. Two retained durations fall less than 0.3 ms below their nominal boundaries. Both counts and actual elapsed times are retained; throughput uses actual elapsed time. LocalStack request logs include setup and cleanup; `confirmed-requests` omits warmup so measured input counts can be used to calculate messages per selected data-operation request.

`analysis.json` contains medians, ranges, CPU, allocations, memory and totals per profile/variant. All counts include only measured inputs, excluding warmup. Hardware was recorded during the run in `host.json`; this was a shared development host. Broker limits are in the checked-in compose file. No AWS account was contacted.
