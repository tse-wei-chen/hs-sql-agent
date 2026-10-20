# SqlCore compiler microbenchmark

Run from repository root:

```bash
dotnet run -c Release --project backend/benchmarks/HsSqlAgent.SqlCore.Benchmarks -- --iterations 1000 --warmup 100 --output artifacts/sqlcore-perf.json
```

Use `--smoke` for a **quick compilation/determinism check only**; do not compare smoke
latency numbers. The manual workflow `SqlCore benchmark` runs in Release and uploads its
JSON report. PR CI runs just the smoke mode inside the existing syntax job.

The runner measures **end-to-end parse -> bind -> normalize -> validate -> render** on four
stable representative shapes across all six source/target-matched dialects. For each case
it emits mean and p95 elapsed microseconds, plus mean current-thread allocated bytes.
It also checks SQL text, parameter count, and plan fingerprint for determinism.

## Interpretation

- These are microbenchmark measurements, not production latency/SLO guarantees.
- Compare only results from pinned .NET SDK/runtime, hardware, GC and runner environment.
- Cross-dialect translation, huge inputs, pathological nesting, type metadata, I/O,
  and database execution are **not** measured by this initial harness.
- No performance percentage or regression threshold is asserted before establishing a
  repeatable baseline with noise characterization.
- Benchmark results contain no user SQL, credentials or connection information.
