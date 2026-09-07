# Allocation follow-up data

See [ALLOCATION_RESULTS.md](../../ALLOCATION_RESULTS.md) for findings and revision boundaries. `raw-results.tar.gz` contains all 76 untraced trials, request counts, logs, source patches, options and binary hashes. `summary.json` and `summary.csv` preserve each profile separately, including original anomalous checks and their repeats. `final-validation.json` records delivery and fingerprint checks.

Main optimization: `0b3dfdc86687ab55d2ee6037e608fc97ac0a748d`. Final BOM-compatible code: `e677cf9a4c53fdea468344f175a4d40f2a698c9a`. Previous pipeline binaries: `77c20ea354919fd25ae300e49c5de7f3ed8da598`. The scripts preserve the exact Linux paths used; adapt the snapshot paths when replaying elsewhere. Main cross-platform benchmark commands remain in the benchmark README.

The accompanying local artifact archive includes complete nettraces, allocation-stack JSON, the standalone TraceAnalysis reader, both optimized binary snapshots, test/build logs and a Git bundle. The official runtime and previous binary snapshots remain in the preceding pipeline artifact archive.
