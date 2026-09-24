# Raw messaging benchmark evidence

`raw-trials.tar.gz` contains all 141 JSON trial records, worker logs, per-profile metadata and summaries. CSV/Markdown summaries and the two failed trial records are also kept here for direct review. Native dumps remain local and are not included.

- `standard` and `extended`: initial complete matrix before the in-memory timer fix.
- `memory-fixed` and `memory-extended-fixed`: repeated in-memory comparisons after commit `6a1a9887`.
- `soak`: two-minute trials after that fix; the failed Redis warmup and its labeled second attempt are both retained.
- `rate` and `loopback`: the same built executable as `soak`, invoked individually. Options and runtime/library versions are in each JSON; host/runtime metadata is shared with `soak`.

Standard configurations have three trials. Extended, offered-rate and loopback cases have one each. Failed workers do not contribute successful measurements. The regular output directory is ignored by Git; this is an intentional baseline snapshot.

Extract and regenerate a summary in PowerShell:

```powershell
$results = 'benchmarks/Messaging/results/baseline-20260906'
New-Item -ItemType Directory -Force $results | Out-Null
tar -xzf benchmarks/Messaging/baselines/2026-09-06/raw-trials.tar.gz -C $results
./benchmarks/Messaging/summarize.ps1 -Directory (Join-Path $results 'standard')
```
