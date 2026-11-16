# SqlCore v3.0 U0 — Baseline and compatibility acceptance

**Owner:** `feature/sqlcore-v3-baseline-contracts` · **Target:** `develop`  
**Source baseline:** `4f9ca434cdc72c8a5aaff128d0626b1e2ffa2d9d` (2026-11-15, #648).  
**Scope:** U0 / F06 / T05 / T06. No changes to production `Rewrite*.fs`.

## What the snapshot means

The existing `RewriteScriptParser.fs` calls the typed Query/Mutation parser, but returns
`Unsupported` for the other semantic families in **ParseScript**. This is a
**syntax-entrypoint snapshot**, not a judgment on `AnalyzeScript` capability or
the three distinct compile APIs, and does not imply execution permission.
Do not convert an existing `Unsupported` cell to `Parsed` just to satisfy a test.

### Eight-family × six-dialect ParseScript contract

| Family | Fixed SQL fixture | PostgreSQL | MySQL | SQL Server | SQLite | Oracle | Firebird |
| --- | --- | --- | --- | --- | --- | --- | --- |
| DQL | `SELECT 1` | Parsed | Parsed | Parsed | Parsed | Parsed | Parsed |
| DML | `DELETE FROM users WHERE id = 1` | Parsed | Parsed | Parsed | Parsed | Parsed | Parsed |
| DDL | `CREATE TABLE users(id INTEGER)` | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |
| DCL | `GRANT SELECT ON users TO analyst` | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |
| TCL | `COMMIT` | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |
| Session | `SET search_path TO public` | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |
| Utility | `EXPLAIN SELECT 1` | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |
| Procedural | `CALL run_job()` | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |

This matrix reflects the **current parser route** inspected at the source SHA.
The C# test freezes the 48 outcomes; `--v3-inventory` independently records
actual statuses, batch indexes, absolute spans and typed diagnostics on CI.
`SELECT FROM`, malformed UPDATE and unknown statement controls are recorded
separately. A status assertion that disagrees with the real build is an audit
finding: correct the baseline evidence, **not** the SQL compiler.

## Compilers, GO, SourceSpan and diagnostics

- Each provider records valid, invalid and wrong-family results from
  `TryCompileQuery`, `TryCompileDml` and `TryCompileDdl`.
  `ParseScript` being Unsupported for DDL does **not** mean `CompileDdl`
  is unavailable. C# tests pin a minimal proven example for each domain.
- SQL Server `GO` is a physical client-side batch boundary only when
  standalone on its line; comments are trivia. It is never a T-SQL execution grant.
  Other providers may not reinterpret `GO` as a separator.
- Each statement's `Text` must equal the exact source slice
  `sql.Substring(Span.Start, Span.Length)`; typed diagnostic spans and stages
  are recorded separately. Unknown or invalid input must fail closed.
- `SqlCoreFacade`, option DTOs, Try results and script projections remain
  C#-consumable without `Microsoft.FSharp.*` in public signatures.
  This does not ban the runtime `FSharp.Core` assembly dependency.
- Cancellation is propagated as `OperationCanceledException`, not an ordinary
  unsuccessful `TryCompile` result.

## Reproducible reports

From repository root:

```bash
dotnet run -c Release --project backend/tools/HsSqlAgent.SqlCore.Benchmarks -- --v3-inventory --output artifacts/v3-inventory.json
dotnet run -c Release --project backend/tools/HsSqlAgent.SqlCore.Benchmarks -- --v3-abi --output artifacts/v3-public-abi.json
dotnet run -c Release --project backend/tools/HsSqlAgent.SqlCore.Benchmarks -- --v3-perf --warmup 100 --iterations 300 --output artifacts/v3-perf.json
```

The fixed workload includes all eight ParseScript families plus Unknown/Invalid,
three TryCompile* domains and 64/256-statement lexical scan workloads on each
of six dialects. Every measured invocation is checked against a deterministic
semantic digest **outside** timing/allocation measurement. Release reports
contain p50/p95 microseconds, median and mean current-thread allocated B/op,
plus runtime/OS/architecture. Results are microbenchmarks, not SLOs.

**Token scan corpus vs measured scans:** script inputs are fixed and named
`token-scan-script-64/256`; actual lexer pass and AST walk counts are *not*
exposed by this instrumentation. The report explicitly uses
`measuredTokenScans: null` and `measuredAstWalks: null`. Existing optional
`--stage-profile` reports stage WorkUnits, not a guaranteed token count.
Do not represent these as measured per-token scans. T06 future instrumentation
may add this separately after verifying an auditable source.

## CI and ABI freeze

`sqlcore-v3-baseline.yml` checks out the **exact** PR head and
`develop` base SHA, copies the same benchmark harness onto both checkouts,
builds the respective SqlCore code in Release, and archives:

- Full public exported-type/member CLR ABI signatures and base/head missing
  signature evidence. Removed or changed signatures fail CI; additions remain
  reviewable. It also packs both NuGet assemblies.
- 48 family cells and invalid/unsupported/compile/GO inventories. Unexpected
  changes to status, diagnostic or output evidence fail CI for review.
- Matched fixed corpus p50/p95 / B/op and output digests. Digest mismatch fails;
  numeric latency/allocations stay **observational** until a stable noise floor
  exists; do not install an arbitrary hard 5% cutoff on GitHub-hosted runners.
- C# compatibility assertions on `net8.0` and `net10.0`.

This is an ABI comparison between built assemblies, **not** byte-for-byte
NuGet ZIP equality (which would include non-ABI timestamp/build metadata).

## Acceptance evidence

- [x] Correct base SHA confirmed and dedicated feature branch from `develop`
- [x] No `Rewrite*.fs` modifications
- [x] Test contracts for eight families, six dialects, positive/negative and GO
- [x] Deterministic inventory, public ABI and fixed performance corpus harness
- [ ] PR head's complete required CI set verified green
- [ ] Actual artifact outputs inspected; classification and compiler results accepted
- [ ] Merge only after review and all required CI green

**Stop rule:** Once U0 contracts and reports reproduce and required CI is green,
merge this baseline only; U1 dispatch consolidation is a different feature.
