import collections, json, pathlib, re
root = pathlib.Path('/tmp/foundatio-allocations')
repo = pathlib.Path('/tmp/foundatio-pr-533-review')
rows = json.loads((root / 'summary.json').read_text())
lookup = {(r['Profile'], r['Variant'], r['Workload']): r for r in rows}
validation = json.loads((root / 'final-validation.json').read_text())
lines = ['# Messaging allocation results', '',
    'The default JSON serializer now writes directly to an owned byte array and reads directly from input memory. Existing serializer extensions select the optional `IBufferSerializer` capability automatically. AWS sends also avoid intermediate dictionaries and single-message batching lists, and receive requests omit unused system attributes. The public messaging calls and wire format are unchanged by this allocation pass.', '',
    'The repeated optimization matrix measures `0b3dfdc86687ab55d2ee6037e608fc97ac0a748d` against the previous pipeline implementation, `77c20ea354919fd25ae300e49c5de7f3ed8da598`. Final code is `e677cf9a4c53fdea468344f175a4d40f2a698c9a`, which additionally preserves UTF-8 byte-order-mark handling. Its full test suite, 20 additional load trials, and two allocation traces passed. Results from those revisions/profiles are kept separate below.', '',
    f'All **{validation["Trials"]} untraced trials passed**: **{validation["Inputs"]:,} inputs and {validation["Deliveries"]:,} acknowledged deliveries**, with zero missing, duplicate or invalid deliveries, zero tracking-limit failures and zero benchmark worker crashes. Five diagnostic captures also passed delivery validation; their performance totals are excluded from comparison medians.', '',
    '## Repeated AWS comparison', '',
    'These are median **managed allocated bytes per input**, including SDK and harness work and excluding broker processes. Four-subscriber fanout requires four acknowledged deliveries per input. Each cell uses three fresh-process, untraced trials against LocalStack. Allocation churn is not retained memory.', '',
    '| Payload / workload | Previous Foundatio | Optimized Foundatio | Reduction | MassTransit |',
    '| --- | ---: | ---: | ---: | ---: |']
for payload in (1024, 16384):
    for workload in ('queue', 'fanout'):
        p = lookup[(f'aws-{payload}', 'before', workload)]['AllocatedBytesPerInput']
        a = lookup[(f'aws-{payload}', 'after', workload)]['AllocatedBytesPerInput']
        m = lookup[(f'aws-{payload}', 'masstransit', workload)]['AllocatedBytesPerInput']
        lines.append(f'| {payload // 1024} KiB / {workload} | {p:,.0f} | {a:,.0f} | {100*(1-a/p):.1f}% | {m:,.0f} |')
lines += ['', 'The 16 KiB queue allocation gap against MassTransit is reversed in this matrix: Foundatio allocates about 7% less. Short-run AWS fanout still allocates more: about 5% at 1 KiB and 22% at 16 KiB. The two-minute fanout comparison below has the opposite allocation ordering. Do not generalize one payload or duration to all workloads.', '',
    '| Payload / workload | Previous inputs/s | Optimized inputs/s (range) | MassTransit inputs/s (range) | Previous / optimized / MT p99 ms |',
    '| --- | ---: | ---: | ---: | ---: |']
for payload in (1024, 16384):
    for workload in ('queue', 'fanout'):
        p, a, m = [lookup[(f'aws-{payload}', v, workload)] for v in ('before', 'after', 'masstransit')]
        lines.append(f'| {payload // 1024} KiB / {workload} | {p["InputsPerSecond"]:,.0f} | {a["InputsPerSecond"]:,.0f} ({a["Minimum"]:,.0f}–{a["Maximum"]:,.0f}) | {m["InputsPerSecond"]:,.0f} ({m["Minimum"]:,.0f}–{m["Maximum"]:,.0f}) | {p["P99Milliseconds"]:,.2f} / {a["P99Milliseconds"]:,.2f} / {m["P99Milliseconds"]:,.2f} |')
lines += ['', 'The 1 KiB queue/fanout median throughput changes versus the previous implementation are approximately +3%/+6%; the 16 KiB changes are +4%/+4%. Several ranges overlap. Saturation p99 includes a bounded backlog and final settlement; it is not unloaded request latency.', '',
    '## Final-revision follow-up', '',
    'The initial three-run in-memory queue comparison showed 11% fewer allocated bytes but a 6% lower median rate. Five longer repetitions on final code did not reproduce a consistent slowdown. The initial single Redis fanout check allocated 5% more, so that case was repeated three times. Both original and repeated observations remain in the data.', '',
    '| Profile | Previous / final bytes per input | Previous / final inputs/s (ranges) | Previous / final p99 ms |',
    '| --- | ---: | ---: | ---: |']
for profile, workload in [('memory-repeat', 'queue'), ('redis-repeat', 'fanout')]:
    p, a = [lookup[(profile, v, workload)] for v in ('before', 'after')]
    lines.append(f'| {profile} | {p["AllocatedBytesPerInput"]:,.0f} / {a["AllocatedBytesPerInput"]:,.0f} | {p["InputsPerSecond"]:,.0f} ({p["Minimum"]:,.0f}–{p["Maximum"]:,.0f}) / {a["InputsPerSecond"]:,.0f} ({a["Minimum"]:,.0f}–{a["Maximum"]:,.0f}) | {p["P99Milliseconds"]:,.2f} / {a["P99Milliseconds"]:,.2f} |')
lines += ['', 'The in-memory queue allocation reduction is about 11% across both studies. Redis fanout allocates about 14% less in the repeated study, but its median p99 is higher; there is no uniform tail-latency improvement. The original in-memory fanout study reduced allocation from 26,588 to 24,852 bytes/input (7%) with a median rate of 168,914 versus 181,925 inputs/s. The single Redis queue check reduced allocation from 147,438 to 59,837 bytes/input; that large change has only one trial per implementation.', '',
    'Final code also passed one confirmation per AWS workload and payload:', '',
    '| Payload / workload | Final bytes/input | Final inputs/s | Final p99 ms |',
    '| --- | ---: | ---: | ---: |']
for payload in (1024, 16384):
    for workload in ('queue', 'fanout'):
        a = lookup[(f'aws-final-{payload}', 'after', workload)]
        lines.append(f'| {payload // 1024} KiB / {workload} | {a["AllocatedBytesPerInput"]:,.0f} | {a["InputsPerSecond"]:,.0f} | {a["P99Milliseconds"]:,.2f} |')
lines += ['', 'Small-payload AWS allocation varied materially: the final queue confirmation was 34,473 bytes/input, versus the earlier optimized median of 28,548. The earlier repeated result is not a guaranteed reduction for every run. Exact allocation ranges, CPU, GC pauses, collections and working sets are retained in the summary and raw JSON.', '',
    '## Sustained load and process memory', '',
    'These are single two-minute 16 KiB trials at the optimization revision, with the same 20-million-input tracker capacity. Peak working set includes SDK, harness, fixed tracking arrays and touched pages; it cannot establish leak freedom.', '',
    '| Implementation / workload | Inputs/s | Bytes/input | Peak working set MiB | p99 ms |',
    '| --- | ---: | ---: | ---: | ---: |']
for variant in ('after', 'masstransit'):
    for workload in ('queue', 'fanout'):
        a = lookup[('aws-soak', variant, workload)]
        lines.append(f'| {variant} / {workload} | {a["InputsPerSecond"]:,.0f} | {a["AllocatedBytesPerInput"]:,.0f} | {a["PeakWorkingSetMiB"]:.1f} | {a["P99Milliseconds"]:,.2f} |')
lines += ['', '## Allocation attribution', '',
    'GC-verbose EventPipe captures cover 1 KiB and 16 KiB fanout before and after, plus MassTransit at 16 KiB. The offline reader weights GCAllocationTick stacks by AllocationAmount64 over seconds 12–30 of each trace. All five windows have allocation stacks and zero reported lost events. These are sampled attribution estimates, separate from untraced allocation counters.', '',
    'The default serializer’s intermediate output-stream growth accounted for **6.46%** of weighted allocations in the previous 16 KiB trace and had **no samples** in the final trace. The allocation regression test independently failed before the fix at **10,012,800 allocated bytes for 5,000,200 output bytes** and passes with the buffer path under a 1.25× output-size budget.', '',
    'Largest remaining 16 KiB sampled sites include application payload strings (21.3%), SDK response strings (20.5%), SDK receive checksum buffers (11.1%), and Foundatio’s owned receive-body byte arrays (9.3%). The last buffer keeps raw-message, retry and dead-letter payloads independently owned. Checksum validation and the delivery guarantees were retained. Removing these remaining copies would need a separate ownership or SDK change; none is claimed here.', '',
    '## Validation and reproducibility', '',
    '- Final Release solution build passed for net8.0 and net10.0; only the pre-existing ASPIRE010 warning remains. The sibling-repository aggregate solution is unavailable in this isolated checkout.',
    '- Final suites: **2,193 passed, 24 expected skips, zero failures** across core, AWS, Redis and benchmark validation. Serializer tests include stream-only implementations, custom options, nulls, runtime types, Unicode, primitives, sliced/non-array memory, and BOM-prefixed JSON.',
    '- AWS tests cover automatic and explicit batches, byte limits, native headers, malformed envelopes, missing/duplicate response IDs, partial failure, cancellation, disposal and acknowledged settlement.',
    '- Documentation build and changed-file whitespace checks passed. No dependencies were added to the library.',
    '- Benchmark workers use the official Microsoft .NET 10.0.11 runtime, MassTransit 8.5.10 and matching SDK binaries. CoreCLR SHA-256: `3EBE90CD92B1EDF6742A41FA921A0C6326216FD1CCA45FDB5E055BEA33351BEA`.',
    '- LocalStack 3.8.1: four CPUs, 3 GiB limit. Redis 8.6-alpine: four CPUs, 2 GiB limit, AOF every second. Main trials publish for 15 seconds after up to three seconds of warmup; the memory repeat uses 30 seconds and up to five seconds of warmup, and Redis repeat uses 20 seconds and up to five seconds. Warmup is capped at one million inputs.',
    '- All measured workers run sequentially, without concurrent builds, tests or profiling. Other host applications are left running, so small changes and overlapping ranges require caution. No live AWS account was used; the existing explicit live mode is preserved.',
    '- Cleanup verified zero fperf queues, topics and Redis keys. The two task-owned containers, network and LocalStack anonymous volume were removed; conformance resources were removed with those containers.',
    '- The earlier Ubuntu-runtime native crashes remain unresolved; this pass uses the preserved official runtime and does not alter the system installation.', '',
    'Raw trial data, scripts, configuration, hashes and summaries are in [baselines/2026-09-07-allocations](baselines/2026-09-07-allocations/). The accompanying local artifact archive holds complete nettrace captures, allocation-stack JSON, the standalone TraceAnalysis reader, binary snapshots and validation logs. See its methodology for the exact capture command and [Microsoft’s trace documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) for the gc-verbose profile.', '',
    '## Scoped source scan', '',
    'The five changed production serialization/AWS files were checked using the performance skill recipes. Counts below are code signals, not counts of defects; the remaining lists and dictionaries include bounded native requests, explicit batches and cold error/provisioning paths. The two AWS partial declarations are one sealed primary type; the existing public JSON serializer remains extensible (one of two primary class types sealed).', '',
    '| Recipe | Hits |', '| --- | ---: |']
for r in json.loads((root / 'source-scan.json').read_text())['Recipes']:
    lines.append(f'| {r["Recipe"]} | {r["Count"]} |')
lines += ['', 'Three measured allocation opportunities were addressed: intermediate serializer output buffers, single-send batching/attribute scaffolding, and unused receive metadata. No critical pattern was found in this scoped scan. It is not a whole-repository performance audit.', '']
report = '\n'.join(lines)
(root / 'ALLOCATION_RESULTS.md').write_text(report)
(repo / 'benchmarks/Messaging/ALLOCATION_RESULTS.md').write_text(report)
print('Wrote allocation report with all original and follow-up results.')
