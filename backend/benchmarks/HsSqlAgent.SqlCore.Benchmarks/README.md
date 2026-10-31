# SqlCore compiler microbenchmark

The renderer now composes bounded SQL documents, including nested SELECT, CTE,
derived/LATERAL, SET, paging and DML output. Child queries retain document
references rather than copying complete SQL strings into every parent. An
explicit heap traversal emits the final string, checking limits before buffer
growth and the final copy. Identifier escaping and SQL literal expansion reserve
their output before allocating expanded text. Parameter/scope checkpoints use
persistent collections so continuation retries do not copy all previous bindings.

The general schema continuation matrix also runs the full public compiler on
256-KiB stacks: four shapes at 128/512/2048 across six target providers, plus
three PostgreSQL cases with a nested Query in an earlier LATERAL sibling. Tests
verify exact ordered parameter values and all eight successful stages. Nine
multi-CTE cases correctly require the existing target capability rejection;
all other cases must compile. Depth-2048 ceilings remain 80 MB rendering and
240 MB total allocation. A public parsed-AST negative verifies that an oversized
quoted identifier is refused before escaping allocations (under 8 MB in rendering).
Structurally shallow sibling queries use local heap loops; deeper graphs retain
occurrence-scoped continuations. Prefix retries remain metered, so this is not
a claim of arbitrary breadth-linear compilation.

The opt-in `nested-paging-typed-schema` and `invalid-nested-paging` probes
cover all six providers at 128/512/2048 on isolated 256-KiB worker stacks.
Every wrapper owns ORDER BY plus OFFSET/FETCH or LIMIT/OFFSET. CI requires
18 full typed compilations and 18 exact `SQL_PARSE_GRAMMAR` rejections at
the innermost missing operand, preserving its source offset. Negative workers
report `success: false` and a separate `expectedGrammarRejection`; resource
refusals do not satisfy either case. Positive ceilings are 120 MB total and
60 MB rendering; negative allocation is capped at 30 MB. The historic 72-case
depth corpus and 16-case constrained-stack corpus remain intact.

Schema metadata normalization, column scans, qualifier scans and ancestor-scope
walks now charge actual visited items. The wide-schema tests require both proof
phases to count at least 512 columns times 32 references across all six providers.
Real 1024-column / 8192-reference inputs must exhaust the proof work quota through
both string and parsed-AST routes, followed by a successful fresh attempt.
The inverse adapter converts terminal leaves directly, avoiding projection-prefix
replay for these wide inputs. DML constant-predicate policy proofs use metered
heap frames; 36 six-provider 128/512/2048-depth cases on 256 KiB retain the same
deny/allow decisions for provably true and selective predicates.

Run from repository root:

```bash
dotnet run -c Release --project backend/benchmarks/HsSqlAgent.SqlCore.Benchmarks -- --iterations 1000 --warmup 100 --output artifacts/sqlcore-perf.json
```

Use `--smoke` for a **quick compilation/determinism check only**; do not compare smoke
latency numbers. The manual workflow `SqlCore benchmark` runs in Release and uploads its
JSON report. PR CI runs just the smoke mode inside the existing syntax job.

The runner measures **end-to-end parse -> bind -> normalize -> validate -> render** on eight
stable representative shapes across all six source/target-matched dialects. For each case
it emits mean and p95 elapsed microseconds, plus mean current-thread allocated bytes.
It also checks SQL text, parameter count, and plan fingerprint for determinism.

The `SqlCore baseline comparison` PR workflow builds the exact PR head and base
commit in Release on the same runner. It copies the candidate benchmark source
into the base checkout so both compilers receive identical workloads: ordinary
SELECT, CTE, UPDATE, DELETE, INSERT, UNION ALL, and depth-32 derived/scalar queries
on all six providers (48 cases). Three interleaved pairs use 100 warmups and 300
measured compilations per case, with tiered compilation disabled. Reports include
a SHA-256 digest of emitted SQL and ordered typed parameter values; the comparison
fails on missing cases, runtime/settings mismatch, output drift, or invalid data.

Median-of-three broad regression ceilings are 2x base allocation + 4 KiB, 3x mean
latency + 50 microseconds, and 3x p95 + 100 microseconds. These initial ceilings
detect large regressions; they are not a speedup claim or full R4 acceptance.
The artifact includes raw samples, comparison ratios and both commit SHAs.
Deep 128/512/2048 and constrained-stack capability gates remain separate; this
matched shallow corpus does not establish arbitrary SQL stack independence.

First comparison evidence: [run 40088072294](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/40088072294),
base `aebee37845d30e6a8994bc910489948501c620f7`, candidate `21cb010733f48f353f3abbf7a2aa8a5b6b31cec1`,
.NET 10.0.13 / Ubuntu 24.04 / X64. All 48 output digests matched. The table
shows the range of candidate/base median ratios across the six providers.

| Workload | Allocated bytes ratio | Mean elapsed ratio |
| --- | --- | --- |
| DELETE | 1.140–1.170 | 0.981–1.081 |
| INSERT | 1.145–1.174 | 1.015–1.048 |
| UPDATE | 1.199–1.239 | 1.039–1.081 |
| CTE | 1.392–1.458 | 1.167–1.202 |
| Derived depth 32 | 1.526–1.557 | 1.974–2.097 |
| Filter/order | 1.297–1.351 | 1.123–1.159 |
| Scalar depth 32 | 1.836–1.873 | 2.069–2.138 |
| UNION ALL | 1.375–1.436 | 1.200–1.233 |

These results pass the broad regression ceilings but show real overhead versus
main, especially at depth 32; they do **not** demonstrate an overall speedup.
Keep the raw reports and assess these costs alongside the deep-stack safety
and asymptotic allocation evidence before claiming R1/R4 complete.

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
The opt-in `--shapes mixed-query-graph` probes alternating
`Expr → EXISTS → Query → WHERE → Expr` boundaries, including a correlated
outer column at the innermost level. It is **not** part of the unchanged
72-case CI default depth floor: collect its higher-depth failures/typed
resource rejections as observational evidence without masking crashes or
silently redefining the existing baseline.
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

PR CI also records an isolated **PostgreSQL + Oracle 128/512/2048**
mixed-query graph diagnostic report. This is a dedicated regression
**gate** for the supported 128/512/2048-level alternating EXISTS shape:
all six Postgres/Oracle probes must compile without crashes, and each
2,048-level run must allocate no more than 120,000,000 bytes. That
allocation threshold provides more than 4x headroom over the measured
~27.5 MB optimized baseline, yet rejects a return to the >269 MB
quadratic renderer. It is **not** a general SQL-size or production-memory
guarantee, does not change the original 72-case depth floor, and does
not change the constrained-stack safety gate. Failures and resource
rejections are still printed explicitly in the uploaded report.

## Interpretation

- Strict schema/name and type proofs now also use per-verification, occurrence-
  scoped continuations across general Query/Expr/Source boundaries. The isolated
  proof matrix requires **72 cases** (six providers × function-contained Query,
  deeply nested scalar functions, LEFT JOIN-derived sources and multiple CTEs ×
  128/512/2048) to complete both phases on **256-KiB** stacks, report at least
  `depth` real work units in each phase and allocate at most **180 MB combined**
  at depth 2048. Missing-column/type negatives and one public Query object reused
  under different outer scopes verify that proofs cannot leak across occurrences.
  This matrix isolates schema/type from later provider output capability gates;
  it does not claim full compilation for every one of those query shapes.

- Public parser/compatibility conversion now has an additional six-provider
  **108-case 256-KiB stack matrix**: derived, CTE, aliased scalar, mixed
  CTE/derived/scalar, function-contained Query, and LEFT JOIN-derived Query,
  each at 128/512/2048. Both F#→CLR and CLR→F# conversion must actually succeed.
  The general parser uses cursor-local completed-boundary continuations;
  the inverse adapter uses per-call reference-identity continuations. Neither
  grows the CLR stack across Query/Expr/Source boundaries. Suspended prefixes
  can be replayed, with token advances and cache/continuation operations charged
  to the existing work quota. This is not a claim of linear-time parsing for
  arbitrary wide/branching inputs. First syntax-error offsets and inverse AST
  first-error order are tested; cyclic public ASTs fail with a typed resource
  diagnostic. Schema/type proof and rendering retain their separate gates.

- These are microbenchmark measurements, not production latency/SLO guarantees.
- Compare only results from pinned .NET SDK/runtime, hardware, GC and runner environment.
- The opt-in `nested-joined-lateral` and `nested-correlated-exists` probes (with and without
  strict typed snapshots) exercise Query/Source/Expr correlation on an isolated
  256-KiB worker stack at 128/512/2048 nesting. CI fails on crashes,
  timeouts, untyped failures or skipped validation stages. A typed
  `SQL_COMPILER_STACK_RESERVE` refusal is recorded **separately** from
  successful compilation: this gate proves safety, not complete R2
  stack-independent capability.

- The opt-in `nested-correlated-in` probe exercises WHERE identifier IN (Query) under
  256-KiB workers at depths 128/512/2048 with both ordinary and strict typed
  schema. CI requires **six successful compilations**, complete stage
  telemetry and measured 2,048-depth allocation ceilings; stack-reserve
  refusals or crashes fail this capability gate.

- Scalar projection Query continuations: `nested-scalar-subquery` now **must** compile
  with and without strict typed metadata at depths 128/512/2048 on a 256-KiB
  managed stack (6/6 success and complete stage metrics). Depth-2048
  budgets: 32 MB cumulative allocation and 4 MB Renderer allocation.
  `nested-case-scalar` is also **must-compile** with and without typed
  metadata at 128/512/2048 on 256 KiB (6/6 success). CASE depth-2048
  budgets: 90 MB total allocation and 30 MB rendering allocation.
  Typed schema and policy stages must complete; stack refusals do NOT
  satisfy the gate. Other non-linear CASE/Query shapes remain unproven.

- Two dependent JOIN LATERAL sources in one SELECT: the second
  body resolves the first derived alias, while strict schema/type proof uses
  only previously visible sources. The opt-in `nested-double-joined-lateral`
  workers MUST compile 6/6 at 128/512/2048 on 256-KiB stacks (including
  typed phases), with 2048-depth ceilings of 220 MB total / 90 MB rendering.

- Arbitrary-length INNER JOIN prefixes before the final correlated LATERAL
  are now replayed in source order without hardcoding a one- or two-JOIN
  schema/type traversal. The `nested-triple-joined-lateral` isolated workers
  enforce 6/6 actual compile successes with 128/512/2048 depth and typed
  metadata on 256 KiB stacks, with 310 MB total / 120 MB Renderer depth-2048
  allocation ceilings. Non-INNER JOIN, multiple *deep* sibling sources and
  non-linear Query/Expr graphs remain outside this proven path.

- Cross-dialect translation, huge inputs, pathological nesting, type metadata, I/O,
  and database execution are **not** measured by this initial harness.
- No performance percentage or regression threshold is asserted before establishing a
  repeatable baseline with noise characterization.
- Benchmark results contain no user SQL, credentials or connection information.

## G0: auditable production-code delta (v2.1 Lean Code)

Every PR's existing `SqlCore baseline comparison` job now records
`artifacts/code-delta.json` and a GitHub Actions step summary. The script
compares the **exact committed base and PR head SHAs**, not working-tree edits.
It uses `git diff --numstat -z --no-renames` to make cross-category file
moves visible instead of silently recording zero change. The workflow fetches
only the exact base SHA into its shallow candidate checkout.

The JSON separates `production` (`backend/src/Modules` F#/C# sources),
`tests`, `generatedMigrations`, `docs`, and `other`. Each category has
added, deleted, net lines and changed-file counts; binary changes have an
explicit separate count because Git cannot provide accurate binary line
counts. Comments and blank lines are included, as with `git --numstat`.
Moving code to another file or compressing lines can still preserve or
increase semantic complexity; these counts **do not** prove fewer concepts,
better safety or faster compilation.

Read the report alongside the paired six-provider, 48-workload output parity,
mean/P95 and allocation measurements and the independent grammar, live
database, small-stack and policy tests. No new LOC-based hard rejection
threshold is enabled. This closes the G0 **code delta evidence** gap only;
it is not completion of the full G0 semantic-owner audit or G1 DDL compiler.

## G0 R0: compiler-stage profiling and semantic-owner checkpoint

The existing `SqlCore baseline comparison` now runs an independent
**opt-in observational** `--stage-profile` pass on exactly the same 6-provider
× 8-workload corpus and the same benchmark executable. It does not create
another SQL grammar or special-case SQL compiler. The profile attaches to the
*existing* `HsSqlAgent.SqlCore.Compiler` `StageCompleted` DiagnosticListener
inside `RewriteResourceBudget.fs`, collecting per-stage elapsed microseconds,
allocated bytes and metered work units. It verifies 48 workload/provider
combinations, their deterministic output digests, successful stage completion,
and complete per-iteration coverage of Parsing, Binding, Normalization,
Validation, Policy and Rendering. Failure or missing telemetry is a CI failure,
not counted as a zero-cost stage.

Reproduce from repository root:

```bash
dotnet run -c Release --project backend/benchmarks/HsSqlAgent.SqlCore.Benchmarks -- \
  --stage-profile --iterations 5 --warmup 3 --output artifacts/stage-profile.json
python3 .github/scripts/verify_sqlcore_stage_profile.py artifacts/stage-profile.json
```

This is diagnostic, **not** the paired timing baseline: DiagnosticListener
subscription, reflection of telemetry payloads and per-case collection add
observer overhead. Stage timings can be nested and must not be blindly summed
to equal end-to-end elapsed time. Compare the normal three interleaved
base/head runs for performance. Use the JSON profile to locate *candidates*
for independent profiling before optimizing or deleting anything; never treat
an absent stage as proof the semantic responsibility is dead.

### Existing ownership boundaries reviewed (2026-10-31)

| Concern | Existing authoritative code | Why not deduplicated blindly |
| --- | --- | --- |
| Source SQL lexing and grammar | `RewriteLexer.fs`, `RewriteParser.fs`, `RewriteSchemaAnalyzer.fs` for dialect-aware DDL | DDL syntax classification is not executable authorization |
| Shared parsed/bound/normalized document | `RewritePipeline.fs`, `RewriteStages.fs`, `RewriteTypestate.fs` | Required provenance and stage order are distinct from Host execution |
| Query policy and mutation policy | `RewriteQueryPolicy.fs`, `RewriteMutationPolicy.fs`, `RewritePolicy.fs` | Row-cap and mutation all-rows policy are *different* security contracts |
| Source/target capability and rendering | `RewriteCapabilityValidation.fs`, `RewriteFacadeAdapter.fs`, `RewriteRenderer.fs` | Provider native proofs cannot be replaced by a generic SQL string switch |
| DDL static effects | `RewriteSchemaAnalyzer.fs`, `RewriteScriptAnalyzer.fs`, `RewriteSchemaDependencies.fs` | Virtual Catalog / AnalyzeScript facts are not a live schema snapshot |
| Synchronous resource budget and diagnostics | `RewriteResourceBudget.fs` | Existing single DiagnosticListener is reused, not duplicated |
| Host security, approval and actual transactions | Host-side DML planning/approval/transaction code | SqlCore must not acquire DB/IO or mint an execution approval |

This review identified **no independently verified unused production semantic
path that is safe to delete in this telemetry-only PR**. No production-code
deletion is claimed. The separate G1 Schema Compiler must reuse the sealed
DDL operation and effects models, not re-parse or convert static `Known` into
live evidence. This report closes the G0 *stage cost observability* gap only;
it does not certify full dead-code elimination, optimized stage allocations,
or completeness of G1.
