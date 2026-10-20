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

## Isolated SQL nesting diagnostics

Run a subprocess-isolated sweep (results are observational, **not** compiler
capability guarantees):

```bash
dotnet run -c Release --project backend/benchmarks/HsSqlAgent.SqlCore.Benchmarks -- --depth-probe --output artifacts/sqlcore-depth.json
```

The sweep launches a fresh process for each combination of all six dialects,
four input shapes (pure parenthesis wrappers, nested scalar subqueries,
prefix-NOT chains, left-deep AND chains), and 8/32/128/512/2048 nesting units.
For targeted investigation, pass `--dialects Postgres,Sqlite`, 
`--shapes prefix-not,left-deep-and`, or `--depths 128,512,2048`.
PR CI additionally records a **non-gating** PostgreSQL 12-case sweep at
128/512/2048 nesting units and uploads `sqlcore-depth-pr.json` for analysis.
The subprocess reports compilation latency and current-thread allocations,
and its parent records process exit status, timeout and wall time.

This matters because a CLR `StackOverflowException` kills its *own process*.
The parent collector can preserve evidence of such failures without killing
the test runner. Each subprocess is capped at 15 seconds. A failed full sweep
is **recorded**, not silently described as passing. `--depth-probe --smoke`
checks a safe 8/32-depth PostgreSQL subset and fails CI for unexpected failures.

The outer process startup and JIT cost affect elapsed results; compare within
the same .NET/runtime/OS environment. These measurements do not justify an
arbitrary `MaxAstDepth` constant.

## Interpretation

- These are microbenchmark measurements, not production latency/SLO guarantees.
- Compare only results from pinned .NET SDK/runtime, hardware, GC and runner environment.
- Cross-dialect translation, huge inputs, pathological nesting, type metadata, I/O,
  and database execution are **not** measured by this initial harness.
- No performance percentage or regression threshold is asserted before establishing a
  repeatable baseline with noise characterization.
- Benchmark results contain no user SQL, credentials or connection information.
