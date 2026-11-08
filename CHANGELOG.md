# Changelog

All notable changes to this project will be documented in this file.

## [3.1.0] - Unreleased

### PostgreSQL 16 physical Custom DML acknowledgement-loss proof (PR #588)

- Add first non-SQLite real-provider tests for two-statement approved PostgreSQL DML: native COMMIT completes before its acknowledgement is deliberately lost, leaving both physical rows committed; native ROLLBACK completes before its acknowledgement is lost after a conflicting concurrent insert, leaving only the independent row.
- The same scenarios now traverse the published Custom MCP Tool, F# SQL compiler, transaction preview/approval and real Npgsql executor. MCP returns distinct non-retryable commit/rollback outcome-unknown codes with the original approval request ID; an independently persisted Admin SQLite Audit Store records corresponding status, category and searchable approval correlation. Direct runtime tests additionally prove the consumed approval session cannot be replayed.
- Physical database state is read using a separate PostgreSQL connection, not inferred from thrown exceptions. This is bounded PostgreSQL evidence only; other four non-SQLite providers, live distributed limiting, and provider-specific connection pools remain open in #575.


### Real Oracle/MySQL unqualified function-resolution safety evidence (PR #587)

- Add differential live Oracle 26ai and MySQL 8.4 tests of native ABS versus same-named application-owned ABS functions. Direct unqualified calls resolve to native numeric ABS; explicit schema-qualified calls physically return an excluded table secret. MCP Query preserves normal builtin SQL and denies the qualified hidden-table calls, with unchanged physical target counts.
- Initial Oracle test hypothesis was falsified by the live engine (native ABS returned numeric 1); no runtime/compiler restriction or fabricated RED is claimed. The follow-up tests document the demonstrated provider-specific boundary, without extending this result to all UDFs, functions or the 3.1.0 security release gate.


### SQLite Query runtime guards against shadowed built-in functions (PR #586)

- Reject execution of F#-compiled Query on an SQLite connection whose actual function registry includes an application-defined implementation colliding with a native built-in name and calling arity. The query is refused before the statement executes, even if its SQL source contains only a canonical `ABS` expression; applies with and without a table whitelist.
- Real file-backed SQLite RED→GREEN tests prove a host-registered `ABS` can read an excluded relation while the MCP Query previously exposed it. Additional controls preserve native `ABS` queries and benign unrelated custom registrations. The check uses same-connection `PRAGMA function_list` metadata, not SQL-text regexes or a global ban on modeled built-ins.
- Other providers, arbitrary non-built-in functions, out-of-band callback effects and unproven privilege/connection role boundaries are not attested by this change; 3.1.0 security release issue #575 stays open.


### Live SQL Server, Oracle and Firebird Query UDF whitelist evidence (PR #585)

- Add independently measured SQL Server 2022, Oracle 26ai and Firebird 5 native-function hidden-relation read controls: the physical database UDF can return a secret from a table excluded by MCP Query whitelist, but typed Policy and the published MCP Host deny the corresponding qualified/quoted invocation. Each case also preserves normal allowed SELECT execution and unchanged physical target counts.
- Wire all three real-provider scenarios to their existing versioned CI services with explicit missing-connection failures. This narrows the physical evidence gap from #575 but does not establish unqualified function shadowing safety, physical read-only enforcement for unrestricted Query-only keys or closure of the 3.1.0 release gate.


### Prevent 3.1.0 publication before security release gate #575 closes (PR #584)

- Add a fail-closed publication guard to both NuGet and Docker release workflows. While security issue #575 is open, a v3.1.0 release cannot pack/push public artifacts; missing Issues permissions, API failure, and malformed status also block publication.
- Apply the same guard to manually dispatched 3.1.0 prerelease NuGet packages so `--version-suffix` cannot bypass the blocker. Add offline open/closed/failure/identity regression tests to the normal Tests workflow. The guard does **not** satisfy the still-open physical multi-provider security tests or replace the release checklist.


### Custom DML physical/Audit approval reconciliation (PR #581)

- Verify published Custom DML through the real SQLite provider with two-statement atomic mutations and a separate persisted SQLite Admin Audit Store. Confirm physical committed rows, rejected-approval zero writes, and an acknowledged COMMIT remaining successful when the Audit Store fails afterward.
- Include the one-time DML approval request ID in the Audit event's structured `Definition`, while retaining the incoming HTTP `RequestId` for transport tracing. Existing Audit keyword search can find the approval ID through `Definition`; no new column, database migration, tool, or public response shape.
- Do not persist raw SQL statement values in the structured Audit definition. This SQLite proof is not yet an independent six-provider unknown-COMMIT/ROLLBACK fault-injection guarantee; the broader 3.1.0 release gate remains blocked.

### Host HTTP, DDL and secondary-Audit error egress (PR #580)

- Stop reflecting untrusted exception messages and stack traces from the global HTTP error handler. Preserve 400/401/404/409/500 status semantics, trace IDs and existing JSON field shape; DML COMMIT/ROLLBACK uncertainty keeps separate machine-readable non-retryable codes and a fixed reconciliation message.
- Keep six-provider DDL runtime diagnostics in server-side audit; MCP failed/rejected/outcome-unknown results return bounded DDL error codes and fixed safe descriptions. The physical outcome and approval request correlation remain unchanged; DDL unknown outcomes explicitly prohibit automatic retry.
- Secondary Audit I/O errors during Query, Metadata, Custom and built-in DML exception handling no longer replace the original classified failure or disclose Audit storage credentials. DDL's request Audit still fails closed **before** any possible schema mutation. Legacy CLR-only semantic update failures no longer echo raw exceptions.
- Regression tests cover all six DDL providers, HTTP error status families, and Query/Metadata/Custom failure-injection with unavailable Audit. No SQL compiler, capability, permission, public DTO signature or database transaction strategy changed.

### DML runtime-profile connection budget

- Close a SQL concurrency-budget gap: built-in `execute_dml_sql`, published Custom DML tools and durable DML completion now acquire an existing SQL execution lease **before** opening a provider connection to verify the runtime dialect/profile. A saturated budget rejects the request without creating a target database connection.
- Preflight leases release on success, cancellation and database connection errors. Preview and commit still use separate leases; no SQL lease is held while waiting for interactive or external approval.
- Add negative saturation tests for built-in and Custom DML and direct preflight cancellation/release tests; the positive multi-statement Custom DML test now asserts no lease remains held when requesting approval.
- No SQL dialect capability or transaction-approval semantics changed; the runtime-profile proof remains mandatory and uses the same provider connection as before.


### Host MCP error-egress safety

- Fix a confirmed external error-disclosure path: structured Query and Metadata MCP errors, and Custom Query/DML Tool failures, could return raw provider/driver exception messages, including SQL text and potentially connection details. Newly classified responses emit stable codes, bounded stages and safe fixed messages; compiler decision codes and stage context remain available.
- Apply the same egress boundary to built-in DML failure and unknown COMMIT/ROLLBACK reporting. The physical transaction outcome remains unchanged: confirmed commits stay committed, and unconfirmed outcomes retain distinct non-retryable error codes and approval request correlation, with explicit reconciliation instructions.
- Keep raw exception context in controlled Audit/diagnostics (subject to existing redaction/access controls); no sensitive provider text is included in MCP error messages. Custom Tool parameter conversion now enters the same guarded error boundary. This does not imply server-side Audit retention, redaction or access controls are a substitute for database least-privilege.
- Query/Metadata/Custom failure-injection tests and typed SQL diagnostic tests confirm that opaque exception messages are not echoed. No new MCP tools, new SQL dialect capability or public CLR result shape introduced.


### Query safety — provider-native function identities under table whitelists

- Correct the unproven function-effects boundary for restricted Query compilation on MySQL, SQL Server, Oracle, SQLite and Firebird. A schema/package-qualified or quoted native function identifier is now policy-denied when a table whitelist is present, because its definition can read additional relations or have provider-specific effects invisible to outer Query Facts. This applies throughout CTEs and nested expressions and does not remove syntax from unrestricted Query compilation.
- Retain modeled, ordinary unqualified Query functions and PostgreSQL's existing function proof/physical read-only fence. Native functions are not categorically unsafe: the denial reflects the missing *effect and relation-scope proof*, not a demonstration that every provider allows physical writes.
- Add five-provider positive/negative compiler test cases and physical negative tests for MySQL stored-function hidden-table reads and a host-registered SQLite function. These prove only the tested shapes; SQL Server, Oracle and Firebird still require independent physical provider evidence before the broader 3.1.0 release gate can close. DB roles must also restrict database-visible objects and privileges.


### Host security — MCP key explicit read-only defaults (PR #576)

- Correct a verified least-privilege default gap: when API callers omitted or cleared `AllowedTools`, newly issued keys inherited the historical unrestricted custom-tool authorization. New issuance and explicit key edits now persist the four canonical read/query tools (`get_schemas`, `get_tables`, `get_columns`, `execute_query_sql`) instead of a wildcard. Built-in DML/DDL already required explicit grants; this change does not claim they were bypassed.
- Newly provisioned bootstrap keys with omitted tool configuration receive the same default and retain it on subsequent startup reconciliation. Existing legacy keys with null/empty grants are not automatically migrated or revoked; historical unrestricted grants remain visible as legacy, and rotation/clone preserve the source key's established scope. Administrators should inventory these keys and explicitly edit their tool grants.
- Frontend issuance/edit now requires at least one selected tool, describes zero selection as invalid rather than unrestricted, and distinguishes old wildcard keys. No new MCP tool or SQL compiler functionality is introduced.


### 3.1.0 release scope (candidate)

- **Query safety:** PostgreSQL execution uses physical read-only transaction and built-in function/operator fences for specifically attested shapes. This is not a blanket claim that all SQL functions or other providers have no side effects.
- **SqlCore capabilities:** expand typed Query/Window, native DML and Schema Compiler / DDL subsets only where source/target versions, policy and real-provider proofs are available. Unproven syntax continues to fail closed.
- **Host governance:** Query, DML and DDL retain separate execution boundaries. Mutation/schema changes require explicit opt-in, permissions, preview, approval and live revalidation; provider transaction/implicit-commit behavior is not interchangeable.
- **Compatibility and release:** retain the default four safe MCP tool selections, DML/DDL disabled by default, previous CLR constructor compatibility and first-party package/tag publication gates.
- **Evidence:** six-provider corpus and real database tests demonstrate bounded proven cases, **not** complete SQL grammar support. Final release date and promotion to `main` or tags remain maintainer-controlled.


### Host Query — PostgreSQL built-in operator resolution fence

- Fix a physical PostgreSQL 16 MCP Query privilege-boundary bypass: a writable schema placed before `pg_catalog` in connection `search_path` can shadow built-in `=(integer,integer)` with a SQL function that reads a table excluded by the MCP whitelist. A real RED test demonstrated that a whitelisted JOIN returned a secret-derived boolean under this path; the earlier READ ONLY transaction and named-function policy did not catch it.
- Under the existing PostgreSQL Query transaction, put `pg_catalog` first in the transaction-local `search_path` before executing the compiled command while retaining the previous relation schema search order behind it. The rollback restores the original search path, verified with a single-connection Npgsql pool where connection reset is disabled.
- No changes to F# parser/AST, other five providers, Query permissions, DML/DDL approval or public API; tests cover the real operator override, normal equality result, physical snapshot and pooled session restoration. This does not prove safety for arbitrary custom operators/types with no catalog implementation; use restricted database roles and schema CREATE privileges.

### Host Query — PostgreSQL physical read-only execution fence

- Correct the verified PostgreSQL Query tool vulnerability where an allowed SELECT calling a native VOLATILE function could UPDATE physical table rows. A real PostgreSQL 16 Testcontainers RED run demonstrated the mutation through `SqlAgentTool.ExecuteQuerySql`.
- For PostgreSQL only, after compiling and verifying the runtime server profile, execute each immutable SELECT inside an explicit PostgreSQL transaction with `SET TRANSACTION READ ONLY`. Bind the exact transaction to Dapper execution and roll back after buffering results. Both `ExecuteAsync` and `ExecuteWithFactsAsync` share the same gate; SQL/parameters, table policy, audit and the Query/DML/DDL distinction are unchanged.
- Close the independent PostgreSQL whitelist/function-read gap: a physical RED test proved pg_catalog.query_to_xml could read excluded tables inside a read-only transaction. For PostgreSQL Query plans with table whitelist, the domain-owned F# policy permits only reviewed argument-local pg_catalog functions, pins safe built-in calls to pg_catalog and rejects arbitrary qualified/native UDFs. This is a Query policy restriction, not six-dialect grammar removal; DB-level least-privilege permissions remain mandatory.
- Add independent PostgreSQL real-DB cases for 32 stacked Query→mutation attempts, five whitelist bypass variants, native function sequence effects, a database VOLATILE UPDATE function, safe parameterized injection-like literals, and valid OR/CTE controls. Do not claim other five providers are covered by this PostgreSQL runtime fence. The database-specific read-only transaction does not establish that arbitrary SELECT has no external effects (for example advisory locks).


### G11 — SQL Server ordered composite-key projection proof reuse

- Replace duplicated SQL Server CTE key renaming/ordinal proof with the
  strict derived-key projection helper. Preserve direct-column-only, injective,
  complete ordered physical UNIQUE-key lineage and declared CTE alias handling.
- Admit one additional (third) bounded, non-lateral derived SELECT layer for
  native SQL Server UPDATE/DELETE through independent static cardinality and
  executable physical-key proofs. Fourth-layer nesting, computed/repeated/
  shuffled keys, filters, multiplicative joins and unverified targets still
  fail closed. Real SQL Server 2022 live tests cover both operations, rejected
  shapes, one-use approval, schema drift and transaction-time attestation.
  No Host permission or public API changes.

### SqlCore observed SQL coverage ledger (P0)

- Add a deterministic CI ledger of existing checked-in six-provider positive compiler corpora, split by SQL statement family, provider, and declared version-profile evidence. Reject missing or duplicated fixtures, unknown providers, native target mismatches and unexpected count drift. CI publishes a JSON evidence artifact in the existing syntax-matrix job; no second test job, NuGet API or execution path.
- Deliberately report **no official grammar coverage percentage** and **no Host execution parity claim**: the current 4,883 fixture cases do not represent the denominator of supported SQL syntax, and negative/live behavior remains independently gated.

### G10/Q02 — FIRST_VALUE/LAST_VALUE IGNORE/RESPECT NULLS with explicit frame parity

- Extend the existing closed F# window grammar and proof to native SQL Server 2022+ / Oracle 12.1+ FIRST_VALUE/LAST_VALUE. Preserve explicit IGNORE/RESPECT between function and OVER, and the caller's typed ROWS frame: full-partition LAST_VALUE is deliberately different from its default current-row frame.
- Source and target require matching explicit ServerVersion profiles; FIRST/LAST NULL treatment is restricted to the same provider until cross-provider NULL ordering/frame equivalence is proven. SQL Server 2022 CU4 remains a **LAG/LEAD IGNORE** fence only; FIRST/LAST IGNORE is admitted on 16.0 RTM. Reuse the same native renderer and structured CLR DTO, plus six-provider positive/negative unit tests and independent real SQL Server/Oracle row parity. No new driver, MCP tool, Host execution, authorization or second compiler.

### G10/Q02 — Native SQL Server/Oracle LAG/LEAD IGNORE/RESPECT NULLS

- Preserve `LAG/LEAD(value) IGNORE NULLS OVER (...)` and explicit `RESPECT NULLS` in a closed F# null-treatment DU, source and target version profiles (SQL Server 2022+ for RESPECT NULLS, SQL Server 2022 CU4 / build 16.0.4035.4+ for IGNORE NULLS, Oracle 12.1+ for both), function placement and native renderer. A 16.0-only SQL Server profile does not prove the CU4 correctness fix. PostgreSQL/MySQL/SQLite/Firebird remain rejected; no lowering to semantically different ordinary LAG/LEAD.
- Add binary-compatible CLR WindowSpec and structured WindowDefinition enum properties while retaining default and constructor behavior. Actual SQL Server/Oracle parity, six-provider source/target grammar negatives, malformed modifiers and illegal-function cases. No new MCP tool, driver, Host permission or DML/DDL execution path.

### G10/Q02 — MySQL/SQL Server conditional DISTINCT COUNT

- Extend the existing closed F# conditional COUNT shape with one direct-column `DISTINCT` case. For a valid native FILTER source such as PostgreSQL, render MySQL `COUNT(DISTINCT CASE WHEN ... THEN column END)` or SQL Server 64-bit `COUNT_BIG(DISTINCT CASE WHEN ... THEN column END)`. NULL inputs, duplicate values, false and unknown predicates, grouped and global counts retain their target SQL aggregate semantics; 3 real databases cover the bounded integer parity matrix.
- Reject `COUNT(DISTINCT ...) OVER (...)` at the existing semantic window gate, computed/multiple DISTINCT arguments, volatile/subquery predicates, and invalid MySQL/SQL Server raw FILTER source grammar. New `aggregate.filtered_distinct_count` entry does not relax general `expression.filter`; no new public APIs, drivers, permissions, or MCP tools.

### G10/Q02 — Bounded MySQL/SQL Server conditional COUNT lowering

- Prove and render MySQL `COUNT(CASE WHEN ... THEN ... END)` and SQL Server `COUNT_BIG(CASE WHEN ... THEN ... END)` for PostgreSQL-source `COUNT(*)`/`COUNT(direct_column)` `FILTER (WHERE ...)` on MySQL and SQL Server. Reuse the closed F# FilteredAggregate graph; one typed COUNT/predicate shape proof handles capability validation and rendering. Preserve false/NULL/empty and per-frame counts; fail closed on SUM, DISTINCT, computed operands, volatile/subquery/outer-reference predicates and invalid MySQL/SQL Server FILTER source syntax.
- Add a dedicated `aggregate.filtered_count` capability entry while the unrestricted `expression.filter` capability remains rejected; real PostgreSQL/MySQL 8.4/SQL Server 2022 comparison tests and syntax negatives, with no new MCP tools or public CLR APIs.

### G10/Q02 — SQL Server source RANGE offset validation

- Reject T-SQL numeric `RANGE n PRECEDING/FOLLOWING` at source parsing and typed source capability proof even when the selected target supports numeric RANGE. Reuse one typed frame-offset predicate for source and target validation; preserve SQL Server legal `ROWS` / unbounded `RANGE` and PostgreSQL/SQLite native numeric RANGE behavior. Add negative/positive compiler tests and real PostgreSQL/SQLite parity; no new public API, MCP tool or runtime dependency.

### G10/Q02 — Native PostgreSQL/SQLite EXCLUDE window-frame parity

- Preserve explicit `EXCLUDE CURRENT ROW / GROUP / TIES / NO OTHERS` for native `ROWS / RANGE / GROUPS` frame membership. The F# closed exclusion DU, parser, full-query capability proof, validator and renderer own this feature; the additive CLR compatibility enum, existing constructor overload, and structured `WindowFrameDefinition.Exclusion` roundtrip without an alternate SQL path or new MCP tools.
- PostgreSQL 11+ and SQLite 3.28+ are the only proven native source/targets; explicit older versions and MySQL/SQL Server/Oracle/Firebird reject. Actual PostgreSQL 16 and SQLite peer/NULL-aware aggregate parity, negative source/target grammar and version checks, and existing four-default-read-only/DML-off behavior are tested. No `ROWS`/`RANGE` substitution for `EXCLUDE`.

### G10/Q02 — Native PostgreSQL and SQLite GROUPS window frames

- Extend the F# closed window-frame AST and exhaustive CLR compatibility projection with native `GROUPS` peer-group frames. PostgreSQL 11+ and SQLite 3.28+ preserve `GROUPS BETWEEN ... AND ...` without rewriting it to non-equivalent `ROWS` or `RANGE`; other four dialects reject unproven source/target semantics.
- Require window `ORDER BY`, validate bound ordering and explicit source/target version profiles, and expose `window.groups` in the provider capability matrix. Actual database versions still require Host verification. TDD covers positive/negative grammar, version gating and PostgreSQL 16/SQLite live peer-group parity; existing DML approval and the default four read-only tools with DML disabled are unchanged.

### G9 — SQL Server 2022 native MERGE NOT MATCHED INSERT with one-use physical key absence proof

- Add one exact SQL Server `MERGE INTO dbo.table AS t USING (VALUES(...)) AS s(...) ON t.id=s.id WHEN NOT MATCHED THEN INSERT(...) VALUES(s...)` Host Preview → Approval → Commit. One physical non-null non-identity INT PRIMARY key, sole immediately enforced simple UNIQUE key; 1–3 distinct exact native INT payload columns. The INSERT clause must explicitly cover **every actual target column exactly once**, each directly mapped to its same-named source field; different source/INSERT column order is supported. Other provider and MERGE source/action shapes fail closed.
- Preview checks physical target PK presence, shows the immutable proposed full INSERT payload only when missing, and seals its values, exact compiled SQL, match-query fingerprint, actor/policy, full Schema/index digest and row-set hash to an existing one-use challenge. A present key predicts 0 affected rows; absent key predicts 1. Commit acquires transaction-held SQL Server `SERIALIZABLE` `TABLOCKX/HOLDLOCK`, reattests the real Catalog and key state **before** any write, cancels on drift or key/index revocation, and commits only when the actual mutation affected exactly the approved 0/1 rows. Strong table locks intentionally constrain concurrent writers.
- Reuses the F# closed MERGE compiler and #543/#544 native Host physical catalog, strict snapshot, one-use approval and DML coordinator. No extra MCP tool, public API method, database driver, migration or independent transaction executor. Reject defaults, omitted columns, alternate UNIQUE, triggers/foreign key side effects, extra match predicates, expression/NULL/coercion sources, UPDATE/DELETE mixed actions, composite/multirow keys and MERGE atomic batches. Default four selected read-only MCP tools and disabled DML remain unchanged.

### G9 — SQL Server 2022 native MERGE MATCHED UPDATE with sealed old/new value approval

- Extend the existing one-row MERGE MATCHED DELETE Host proof to a bounded **MATCHED UPDATE** of **1–3 direct, distinct, non-key INT columns** from one exact `VALUES` row. The single exact native INT PRIMARY key is still the sole enforced physical UNIQUE key, with complete Schema/index/trigger/side-effect attestation; no casts, expressions, nullable/identity columns, extra predicates, UPDATE key assignment, mixed INSERT/DELETE or atomic MERGE batches.
- Preview reads all affected physical **before-values**, displays immutable **after-values** and fingerprints the whole old-value row alongside the compiled mutation, match SQL, schema digest, policy and actor in the existing one-use approval. Commit holds SQL Server `SERIALIZABLE` `TABLOCKX/HOLDLOCK`, rechecks full schema and every approved old-value, and enforces exact affected-row count before confirming a write. Changed payload without a changed key fails closed, not overwritten. Missing source match affects zero rows. Strong table-level locks limit parallel writer throughput.
- Uses the same F# MERGE compiler, typed Host DML coordinator and existing `execute_dml_sql` entrypoint. A new approval-mode enum member is required internally; **no new MCP tool**, SQL driver or DB migration. All six-provider safety fences and the four default read-only selections with DML off are preserved.

### G9 — SQL Server MERGE matched DELETE, actual Host row-set approval

- Promote **only** a single native SQL Server 2022 `MERGE INTO dbo.<table> AS t USING (VALUES(<exact INT>)) AS s (<key>) ON t.<key>=s.<key> WHEN MATCHED THEN DELETE` from Compiler-only HOLDLOCK support to a complete `execute_dml_sql` Host Preview → one-use Approval → Commit. Reject UPDATE, NOT MATCHED INSERT, combined actions, multirow/composite-key matches, arbitrary ON predicates, unverified providers and atomic MERGE batches.
- Reuse the F# closed MERGE AST, Strict Snapshot name/shape proof, native SQL Server actual sole enforced non-identity INT PRIMARY key, built-in full column/index/trigger/schema digest, and compiled `MERGE WITH (HOLDLOCK)`. Preview exposes 0 or 1 physical primary-key identity, sealing the actual key, compiled match/mutation SQL, schema SHA, actor and policy in a single-use challenge. The compiled DML may **only** execute after a transaction-held `SERIALIZABLE` `TABLOCKX/HOLDLOCK` target pin, fresh catalog digest and complete approved key-rowset recheck. Changed row presence or revoked key/index/schema cancels without side effects.
- Existing `DmlCoordinator` verifies exact affected-row count and confirmed transaction commit; no new MCP tool, public API, SQL driver, approval coordinator or metadata bypass. Strong target table lock intentionally reduces write concurrency. Default four read-only MCP tools stay selected and DML unchecked; no generic MERGE support implied.

### G9 — PostgreSQL one-row DO NOTHING direct key RETURNING

- Extend the proven single-row native PostgreSQL `ON CONFLICT (id) DO NOTHING` Host approval with **`RETURNING id` only** when the direct projected column exactly matches the sole physically enforced, non-null INT2/INT4/INT8 UNIQUE key. The closed F# strict schema model and native Host structural admission reject non-key, wildcard, duplicate, aliased, qualified, computed and system RETURNING items; SQLite conflict skip remains unchanged and still forbids RETURNING.
- Preview keeps the same key-presence check, one-time approval, schema fingerprint and transaction-held `SHARE ROW EXCLUSIVE NOWAIT` key/index freshness verification. Only the actual PostgreSQL row from a **confirmed inserted and committed** mutation is published; an approved DO NOTHING skip produces an empty result set. Changed conflict state, revoked index and replay fail closed, without result-row leakage.
- Reuse the existing `execute_dml_sql` structured `ReturnedRows`, native compiler and metadata reads. No new public API, MCP tool, driver, migration or atomic conflict batch capability. Default four selected read-only tools and DML-off remain unchanged; strong table-lock write contention remains a deliberate tradeoff.

### G9 — PostgreSQL bounded native conflict UPDATE direct RETURNING

- Allow 1–5 distinct unqualified physical direct columns in `RETURNING` on the already proven single-row PostgreSQL `ON CONFLICT (id) DO UPDATE SET field=EXCLUDED.field` Host approval. Columns must be a subset of the exact physical key + 1–4 values, and the existing F# strict snapshot and Host structural gate both reject wildcards, expressions, aliases, qualified/duplicate or system fields. No broader UPSERT or source dialect capability is implied.
- Preview and one-use approval still bind the entire before/after row, immutable proposed payload, exact compiled SQL and complete physical schema digest. Commit rechecks all values and index/catalog drift under `SHARE ROW EXCLUSIVE NOWAIT` and returns only the actual PostgreSQL output row after a successful atomic commit, including SQL NULL. Stale approvals return no result rows; replay fails closed.
- Reuse existing `execute_dml_sql` structured `ReturnedRows` plumbing. No new MCP tool, public API, driver or metadata round trip. DML batches containing native conflict updates still fail closed. Default four read-only MCP selections and DML off remain unchanged. The table-level writer concurrency cost is unchanged; SqlCore benchmark measures compiler regression, not Host throughput.

### G9/M13 safety fence — Original integer token and quoted-column row identity

- The PostgreSQL Host's typed `ON CONFLICT DO UPDATE` approval now attests each proposed SMALLINT/INTEGER/BIGINT value against the **original F#-parsed source span**. Lexical values such as `1e10`, `4.2e1`, `42.0` are rejected for Host execution approval even if F# accurately normalizes them to integral values. Real plain decimal integers including signed values remain eligible after existing physical range/type, uniqueness and transaction rechecks. The general F# SQL parser is unaffected.
- PostgreSQL quoted case-sensitive key/value columns that alias under the shared row-fingerprint's case-insensitive field lookup are denied (e.g. `id` and `"ID"`); case-folded synthetic before/after label collisions such as `"__ACTION"` are also denied. This is deliberately Host-local, without broadening globally shared six-provider name semantics.
- Keep the already merged M13 TEXT/SMALLINT/INTEGER/BIGINT/SQL NULL scope, physical Catalog proof, one-use approval, `SHARE ROW EXCLUSIVE NOWAIT` commit lock, and default four read-only MCP tools with DML disabled. No new parser, extra DB round trip, public API, driver or MCP tool. See real PostgreSQL RED→GREEN tests and independent SqlCore baseline in the PR.

### G9/M13 — Native PostgreSQL exact signed integer and SQL NULL-aware conflict UPDATE approval

- Extend the existing bounded 1–4-field PostgreSQL `ON CONFLICT (id) DO UPDATE SET field=EXCLUDED.field` typed Host path from TEXT NOT NULL only to **exact TEXT, SMALLINT, INTEGER, BIGINT**, where each physical value column may be nullable or NOT NULL. A proposed string must match physical TEXT, a signed integral literal must fit the exact physical INT2/4/8 range, and SQL NULL is admitted only when the real column permits NULL; reject implicit text→number coercion, float/fractional values, date/time/NUMERIC/UUID and expressions. The single sole immediate BIGINT unique/primary key remains NOT NULL.
- Reuse the closed F# strict DML AST/grammar, the physical full-index/trigger schema digest, sole-key proof, full old-row fingerprint, one-time challenge and `SHARE ROW EXCLUSIVE NOWAIT` transaction-held lock. Preview shows real SQL NULL before/after alongside all other values, including NULL→value, value→NULL and NULL→NULL; commit re-attests type/nullability and every old value before executing the F#-compiled parameterized statement. No new database round trip, MCP tool, public API, dependency or changes to default four read-only tools / DML unchecked.
- Bump the internal conflict-update approval-binding discriminator to `v3`. In-flight M12 approvals must be previewed anew after upgrade. Host write concurrency is still limited by the deliberately strong table lock; SqlCore compilation baseline measures a different path and is not evidence of better Host throughput.

### G9/M12 — Bounded PostgreSQL multi-column conflict UPDATE approval

- Reuse M11's native PostgreSQL typed Host `ON CONFLICT DO UPDATE` plan, Preview, one-time approval and commit-time `SHARE ROW EXCLUSIVE NOWAIT` physical table lock; support **1–4 exactly matching `TEXT NOT NULL` proposed columns**, including new 2–4 value-column cases. The source is one VALUES row with an exact non-NULL BIGINT sole simple immediate PRIMARY/UNIQUE key; assignments must cover every inserted non-key column through `SET column = EXCLUDED.column`, without duplicates, other expressions, NULL or partial updates.
- A permanent ordinary target must consist of exactly the key plus those value columns; no extra columns, indexes, defaults, CHECK/FK constraints, user triggers, rules, inheritance, partitions or RLS. Preview exposes an `INSERT`/`UPDATE` action with **before and after values for every column**; their complete old-row fingerprint, exact proposal, physical catalog SHA and compiled SQL are sealed in the challenge. Commit revalidates all values and cancels the whole operation on any drift, without new MCP tools, APIs, drivers or changes to the default four read-only tools / DML unchecked.
- Updated-column proof and challenge-binding version are intentionally bumped. In-flight M11 previews must be requested again when rolling to M12; pre-existing ordinary DML and `DO NOTHING` flows are preserved. Table-lock writer contention remains the explicit cost. No compiler performance improvement is claimed.

### G9/M11 — PostgreSQL one-row conflict UPDATE with immutable Before/After Host approval

- Extend typed Host DML from `ON CONFLICT DO NOTHING` to the narrowly proven PostgreSQL `ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name` one-row path. The physical table must contain exactly a NOT NULL BIGINT sole simple immediate PRIMARY/UNIQUE key and one TEXT NOT NULL value column, without any defaults, CHECK/foreign keys, extra indices, triggers, rewrite rules, partition/inheritance or RLS.
- Preview binds either INSERT (before value absent) or UPDATE (old actual value shown) plus proposed new value and full old-row fingerprint to one-time approval. Commit takes a `SHARE ROW EXCLUSIVE NOWAIT` transaction-held table lock, rechecks the complete catalog digest and old row, and executes the F# compiled command only once. New/conflicting rows, old-value changes, index drift and changed policy cause refusal.
- Generic UPSERT, arbitrary SET expressions, updating the key, UPDATE WHERE, multiple assignments/rows, RETURNING and DML batch remain denied. Existing SQLite/PostgreSQL DO NOTHING and four default read-only tools with DML unchecked are unchanged. Strong lock contention is an intentional safety-first tradeoff; no throughput improvement is claimed.

### G9/M10 — PostgreSQL native single-row DO NOTHING approval

- Extend the existing SQLite bounded `ON CONFLICT (id) DO NOTHING` Host approval to native PostgreSQL, preserving the F# strict single-row/column shape and one-time row-set approval.
- The built-in PostgreSQL 16 provider must prove the **sole simple immediately enforced (NOT DEFERRABLE) PRIMARY or UNIQUE key**, exact non-NULL INT2/INT4/INT8 physical type, real permanent plain relation, no trigger/rule/partition/RLS, and full index/constraint/catalog fingerprint. Preview binds 1 insert or 0 conflict skip; commit takes a PostgreSQL `SHARE ROW EXCLUSIVE NOWAIT` table lock before comparing full schema and actual conflict state, then executes only the F# compiled parameterized INSERT once. Index drift, a changed conflict outcome or changed approval fails closed.
- The strong table lock deliberately blocks concurrent writers during commit-time proof; this is a narrow safe first slice, not a low-contention general upsert. Partial/expression/composite/multiple unique keys, NULL/text/UUID conflict keys, DO UPDATE, RETURNING, multi-row and atomic batches remain denied. No new MCP tool, public API, dependency or frontend default change.

### G9/M10 — SQLite sole physical UNIQUE conflict approval

- Extend the existing single-row SQLite `ON CONFLICT (id) DO NOTHING` Host Approval from a sole PRIMARY KEY to a **sole simple physically enforced UNIQUE column or ordinary nonpartial UNIQUE index**. Reuse the same F# structural validator, native metadata, one-row payload, preview conflict-state fingerprint, one-time challenge, SQLite IMMEDIATE transaction and live freshness recheck. The key remains an explicit non-NULL exact INT64 literal; no generic UPSERT is enabled.
- Reject multiple/partial/expression/composite unique keys, non-unique indexes, triggers, DO UPDATE, RETURNING, multirow and batch forms. An index revoked between Preview and Commit invalidates the approval before any write. Existing PK behavior is unchanged.
- Real SQLite positive and negative live tests cover insert/skip and schema-index revocation. The internal versioned approval binding distinguishes the new sole-UNIQUE-key proof; stale approvals require a fresh Preview. No new NuGet/public API, MCP tool or driver and no frontend default change.

### G9/M10 — native SQLite PRIMARY KEY `ON CONFLICT DO NOTHING` Host approval

- Add the first **narrow native conflict Host approval** without promoting general Upsert/MERGE: one SQLite `INSERT VALUES` row with an explicit, non-NULL exact INT64 PRIMARY KEY literal; the PK must be the **sole physical enforced unique key**, policy `DmlMaxAffectedRows=1`, built-in native SQLite provider, native target schema capture and commit freshness. Any trigger-bearing target is rejected because trigger side effects are outside the approved one-row count. `DO UPDATE`, `RETURNING`, targetless conflict, multiple rows, alternate/composite/extra unique keys, non-SQLite dialects and atomic batches remain fail-closed.
- Host Preview reads the actual PK match, binds an expected **1 INSERT vs 0 DO NOTHING** result and immutable payload in the one-time Approval challenge. Commit rechecks the exact PK set in SQLite's IMMEDIATE write transaction, pins schema freshness, checks actual affected row count, and rolls back any drift. Existing approval context, policy, nonce, transaction witness and target fence contracts are reused; no new public API, MCP tool, driver, or generic conflict execution path.
- F# Strict Schema Snapshot now proves only the one-row explicit-target SQLite `DO NOTHING` column shape; `DO UPDATE` and other unproven UPSERT assignments remain rejected in strict mode. Physical uniqueness stays a separate Host-owned live proof.
- Real SQLite file-database Host integration and SqlCore positive/negative regression tests cover insert, skip, replay, conflict presence/absence drift, schema/index revocation, ambiguous uniqueness, trigger effects, unproven expressions and batch rejection. Existing four selected read-only frontend tools and DML disabled default are unchanged.

### G9/M10 — SQL Server native single-row MERGE target key-range locking

- Reuse the existing F# closed single-row SQL Server MERGE and
  metadata-backed full PRIMARY/UNIQUE target match-key assurance. Its native
  renderer now always emits `WITH (HOLDLOCK)` on the target before the alias,
  so a transaction can hold a serializable key-range lock for **both**
  matched and as-yet-absent source keys.
- Real SQL Server 2022 CI verifies UPDATE, INSERT, DELETE, exact composite
  UNIQUE-key tuple isolation, metadata rejection of a non-unique target and
  competing INSERT lock-timeout against an absent target key while the
  single-row MERGE transaction remains held. No mock provider or skipped
  SQL Server test; the live cases run only in the existing SQL Server job.
- SqlCore still only compiles. The Host's typed Upsert/MERGE admission and
  existing-row Approval remain **fail-closed**. No new API, MCP tool, driver,
  parser, coordinator, or provider-generic MERGE lowering. Frontend defaults
  remain four read-only tools selected with DML disabled.

### G9/M09 — bounded direct-column NULL predicates for native INSERT SELECT

- Physically attested native SQLite, PostgreSQL, MySQL 8.4 InnoDB and SQL Server 2022
  source-row approval additionally accepts `column IS NULL` and `column IS NOT NULL`,
  including the existing bounded AND/OR Boolean tree. Typed F# `IsNullExpr`
  is consumed by the existing Host structural gate; no SQL text matching,
  new parser, provider driver, MCP tool, public API or approval bypass.
- Each direct source-column NULL test consumes **one of the existing four
  condition-budget units**. Non-NULL `=` literals and `IN` members each
  consume one; direct `BETWEEN` consumes two. The seven AST-node cap and
  positive finite affected-row policy are unchanged. `NULL IS NULL` and
  other constant tests, functions/computed operands, `OR 1=1`, general NOT,
  JOIN/CTE and unproven dialects still fail closed.
- Four-provider actual database integration tests cover nullable source
  columns with NULL and non-NULL values, mixed AND/OR, Preview-to-Commit
  selected row parity, generated target row evidence and approval replay.
  SQLite additionally verifies source row drift and row-cap rejection; the
  four-dialect structural rejection matrix ensures unsafe expressions fail.
- Reuse independent source and target permission, live schema identity,
  transaction-held source locks and full projected-source multiset
  revalidation at commit. Four read-only UI tools stay selected and
  DML stays disabled by default.

### G9/M09 — SQLite bounded INSERT SELECT in ordered atomic DML approval

- Reuse the **existing** 2–16-statement typed atomic DML transaction
  and one-time aggregate approval; admit **exactly one** SQLite native
  physical-source `INSERT SELECT` alongside ordinary INSERT VALUES,
  UPDATE or DELETE statements. No `execute_dml_sql_batch` tool, driver,
  parser, second transaction coordinator or public API is introduced.
- Require the built-in **live SQLite physical Catalog snapshot source and
  verifier**, the existing sealed `NativeInsertSelectProof`, independently
  authorized source and target tables, bounded direct SELECT shape, positive
  finite row cap, strict per-child fingerprint, and aggregate row-count policy.
  Multi-source-select batches and cross-provider batches remain denied.
- The entire batch uses **one SQLite SERIALIZABLE write transaction**:
  re-attest schema and compare each child source's **full projected row
  multiset immediately before that child**, including earlier statements'
  effects. A stale/widened source row set, primary-key conflict, or failed
  child triggers full rollback; the failed batch exposes no generated target
  rows. Actual `RETURNING` IDs become visible **only after all children
  commit**. One-time aggregate challenge replay still fails.
- Real filesystem SQLite integration exercises successful mixed batch,
  post-commit generated row images, aggregate replay denial, source drift,
  earlier-child source widening, second-statement unique-key failure and
  aggregate row-cap rejection. Unsupported provider, multiple source inserts,
  single-statement batch and unverified source metadata are rejected before
  connecting to the database.
- Four read-only tools remain selected by default; DML remains disabled.
- CI: pin the real Firebird Testcontainers fixture to the same `5.0.4-bookworm`
  engine used by the separate Firebird parity job; wait for the TCP port
  (not just the startup log) and retry only transient transport disconnects
  with a finite bound. Missing/unhealthy DB and SQL errors still fail CI.

### G9/M09 — bounded native BETWEEN for four physical INSERT SELECT sources

- Four already-attested native provider source predicates (SQLite, PostgreSQL,
  MySQL 8.4 InnoDB, SQL Server 2022) additionally accept direct
  `column BETWEEN lowerLiteral AND upperLiteral` in the same bounded
  `AND`/`OR` boolean filter. This is a typed F# `BetweenExpr` admission,
  not textual SQL rewriting or a new parsing/compilation path.
- Both inclusive endpoints must be direct non-NULL literals; NOT BETWEEN,
  computed/NULL/column endpoints, literal-only tautologies and subqueries
  still fail closed. Each BETWEEN consumes **two** of the existing
  **four-total-literal** budget (shared with `=` and `IN`), and the
  **seven-boolean-node** limit remains unchanged.
- Real database regression tests prove both inclusive endpoints, optional
  `AND` and `OR` combinations, strict native source row Preview→Approval
  →Commit, post-commit generated IDs, replay, source drift and row-cap denial.
  Physical table Catalog, one-time approval, transaction-held locks and
  actual affected-row parity remain mandatory.
- No added MCP tool, provider driver, public API or changed frontend
  defaults (four read-only tools selected; DML disabled).

### G9/M09 — four-provider bounded native INSERT SELECT IN/OR source predicates

- Existing physically attested **SQLite, PostgreSQL, MySQL 8.4 InnoDB
  and SQL Server 2022** single-/dual-source `INSERT SELECT` approval
  supports direct `column IN (literal, ...)` and parenthesized `AND`/`OR`
  combinations of direct non-NULL literal equalities. This replaces the
  earlier AND-only approval-time source-predicate grammar, not unrelated
  SQL Server CTE/JOIN cardinality proofs.
- Host consumes **only the F# closed SQL AST**, enforcing a combined maximum
  of **4 comparison literals** and **7 Boolean nodes**. NOT, NOT IN,
  `OR 1=1`, NULL members, IN subqueries, computed expressions, excessive
  literals and unproven providers remain fail-closed. No raw SQL whitelist,
  another parser, execution tool or driver is introduced.
- The source physical Catalog, transaction-held locks, complete source-row
  multiset Preview→Approval→Commit recheck, finite row cap and actual native
  affected-row parity remain mandatory. PostgreSQL/SQLite RETURNING and
  SQL Server OUTPUT INSERTED continue exposing real generated target rows
  only after successful commit. Live database integration tests cover
  all four providers, multi-row inserts, source drift and row-policy excess.
- Frontend default remains four read-only tools selected and DML disabled.

### G9/M09 — SQL Server 2022 native INSERT SELECT OUTPUT INSERTED

- Reuse existing real physical single-/two-table source-row approval to admit
  1–32 distinct direct SQL Server `OUTPUT INSERTED.<target column>` items
  for bounded `INSERT SELECT`. F# already parses/compiles the native row image;
  Host retains its direct-column whitelist, physical SQL Server catalog
  trigger exclusions, live no-enabled-INSERT-trigger assurance, transaction-held
  X locks, approved source multiset, one-time challenge and row-count parity.
- A preview contains **source values only**, never generated target IDs.
  Actual `OUTPUT INSERTED.id` / `OUTPUT INSERTED.id, INSERTED.name`
  values become visible only after a confirmed successful commit.
  Stale source rows roll back without exposing result rows. Replays fail.
- Wildcard/aliases/expressions/duplicates, `OUTPUT INTO`, unproven triggers,
  different row images, out-of-cap results and unsupported providers remain
  denied; MySQL 8.4 `RETURNING` remains unavailable. No additional MCP tool,
  SQL parser, driver, public SqlCore API or frontend-default change.

### G9/M09 — SQL Server 2022 single-physical-table INSERT SELECT

- Extend existing bounded SQL Server 2022 native `INSERT SELECT` source-row approval
  to **one ordinary permanent dbo table used as both source and target**.
  Neither the plan nor its schema snapshot can invent a second physical
  entity; a single native ObjectID/definition digest is shared throughout.
- The commit path obtains **one transaction-held TABLOCKX/HOLDLOCK lock** in
  SERIALIZABLE isolation before live catalog reads, then reattests the one
  real table and the complete approved source-row multiset before one native
  insert. Actual row-count parity and one-time approval remain mandatory.
- Real SQL Server tests cover successful generated-ID self-insert/replay denial,
  changed source rows, index drift, same-DDL physical table recreation,
  competing source writer, excessive source rows and unsupported shapes.
  No new Parser/Renderer, MCP tool, provider driver or public SqlCore API;
  other Providers and SQL Server two-table paths are unchanged.
  Strict privileges remain SELECT/INSERT/VIEW DEFINITION on the same table.

### G9/M09 — SQL Server 2022 physical INSERT SELECT source-row approval

- Enable single-statement bounded `INSERT INTO dbo.target(name) SELECT name FROM dbo.source
  WHERE id = 1` only between **two distinct permanent physical SQL Server 2022
  base tables**, using the existing F# native compiler, strict source-read and
  target-write grants, one-time DML approval and complete projected source-row proof.
- The SQL Server Host now attests real physical server/database/object identity,
  creation and modification timestamps, columns, ordered indexes and key
  constraints. `VIEW DEFINITION` is required; user triggers, foreign keys,
  defaults, check constraints, row-level security, temporal/ledger/graph tables,
  computed or hidden columns and unprovable catalog state fail closed.
- Commit uses SERIALIZABLE and stable-order, transaction-held **TABLOCKX/HOLDLOCK**
  on both source and target before any live schema/catalog recheck, then checks
  the exact approved source multiset immediately before native INSERT. This
  strict opt-in proof can block concurrent writes. Schema/source drift, stale
  challenge or lock conflicts deny or roll back without writing.
- Self-source INSERT SELECT, SQL Server OUTPUT, CTEs, JOINs, UPSERT, computed
  projections, unbounded predicates and other unproven providers remain denied.
  Real SQL Server integration cases are routed to the pre-existing
  `sqlagent-fence-sqlserver` CI job. No new MCP tool, public SqlCore API or
  provider driver. Four default read-only tools, DML disabled.

### G9 / M09 — Bounded conjunctive native INSERT SELECT source predicates

- Allow one to four direct `column = non-NULL literal` source predicates joined
  only by `AND` for the existing native SQLite, PostgreSQL and MySQL 8.4
  InnoDB `INSERT SELECT` approval paths. Both same-table and independently
  proven separate-table modes retain their existing provider, version, schema,
  source grant, maximum row count and approval requirements.
- A single iterative closed Host shape gate now rejects over-four predicates,
  `OR`, `NOT`, null/functional/computed comparisons, CTEs, JOINs and unknown
  query shapes; the F# parser/compiler and native SQL rendering remain the
  sole SQL semantic owners. Full projected source values are revalidated in
  the existing transaction-held native schema and row-lock proof before insert.
- Real SQLite, PostgreSQL and MySQL integration tests prove exact approved
  rows and committed mutations; bounded negative gates reject five-condition
  predicates at the correct Host stage. No new MCP tool, driver, public SqlCore
  API, transaction coordinator or change to the four selected read-only tools
  and disabled-by-default DML behavior.

### G9 / M09 — MySQL 8.4 InnoDB same-table INSERT SELECT approval

- Extend native bounded `INSERT INTO db.target (name) SELECT name FROM db.target
  WHERE id = 1` to source and destination being **one actual physical InnoDB
  base table**; retain the existing distinct two-table capability. The same
  strict source projection, predicate, version and positive finite row cap apply;
  CTE/JOIN/UPSERT/RETURNING and multi-statement writes remain rejected.
- Capture **one actual MySQL native schema and InnoDB TABLE_ID proof**, not a
  fabricated two-table snapshot. At preview and commit, an explicit
  `SERIALIZABLE` transaction pins the source with a regular locking read and
  transaction-held metadata lock *before* live schema revalidation. Require
  the original immutable source row multiset, plan fingerprint and one-time
  approval; mutate once and return success only after confirmed commit.
- Real MySQL 8.4 native integration covers approved insertion, approval replay,
  source row and index drift, identical-DDL physical replacement, concurrent
  writer lock denial and insufficient isolation. Existing engine, trigger,
  foreign-key and grant exclusions remain fail-closed. No new MCP tool,
  public SqlCore API, dependency or alternative compiler pipeline.

### G9 / M09 — MySQL 8.4 InnoDB INSERT SELECT source-row approval

- Strict **single-statement, two different real InnoDB base tables** in the
  same non-system MySQL database; explicit unique target columns, direct
  unaliased source projections, `WHERE column = non-NULL literal`, finite
  positive row cap. MySQL **RETURNING**, JOIN, CTE, computed projections,
  same-table source, ON DUPLICATE KEY/UPSERT, multiple statements and other
  previously unsupported providers remain fail-closed.
- New physical MySQL 8.4 native table proof verifies each table's exact
  physical name, InnoDB engine, real `SHOW CREATE TABLE` DDL (including
  column definitions, indexes and constraints), absence of foreign keys and
  triggers, plus **global TRIGGER and PROCESS catalog visibility held by the
  current SQL user**. Binds the actual InnoDB `TABLE_ID` and MySQL
  `@@server_uuid` to the snapshot, denying identical-DDL table replacement.
  Missing privilege or metadata denies approval, never means empty triggers.
- Preview binds complete selected source values and immutable policy/plan
  fingerprints. A `SERIALIZABLE` MySQL commit transaction pins source and
  target metadata locks through ordinary SQL table references (**never
  `LOCK TABLES`**, which would implicitly commit), revalidates both native
  structures and the selected source-row multiset, executes exactly once and
  compares affected rows before commit. Unexpected source/index/DDL drift,
  concurrent source writes, MyISAM and approval replay fail closed.
- Native MySQL 8.4 integration tests cover commit, source values and index
  drift, source locking, nontransactional engine, triggers, FKs, missing
  source grant, over-cap rows and unsupported syntax. No additional public
  SqlCore API, MCP tool, SQL renderer or third-party dependency. UI defaults
  remain four read-only tools selected, DML disabled.

### G9 / M09 — PostgreSQL self-source INSERT SELECT approval

- Extend strict PostgreSQL `INSERT ... SELECT` to the case where the
  explicitly qualified permanent source is **the same physical table** as
  the insert target, including bounded direct-target-column `RETURNING`.
  Retain direct SELECT projections, one literal-equality WHERE predicate,
  positive row cap, full source-value multiset and one-time approval.
- A self-source plan binds **one** independently verified live catalog proof,
  not a duplicate two-table proof. The PostgreSQL SERIALIZABLE transaction
  acquires one `SHARE ROW EXCLUSIVE NOWAIT` lock on that actual table
  **before the first SELECT**; commit verifies relation OID, columns,
  defaults, indexes, constraints and every approved source value before
  inserting once. Preview never invents generated target IDs.
- Case-exact PostgreSQL physical table identity is preserved throughout
  admission and live attestation (SQLite stays case-insensitive). Changed
  source values/indexes, concurrent writes, excess rows and reused approvals
  fail closed. Existing exclusions for JOIN, CTE, ON CONFLICT, RLS, triggers,
  partitioning and unproven providers remain unchanged.
- No additional MCP tools, public API, provider package, SQL compiler or
  approval executor. Four read-only tools remain selected; DML remains off.

### G9 / M09 — PostgreSQL two-table INSERT SELECT approval

- Native PostgreSQL now admits a **single distinct physical source table** and
  **one target table in the same non-system schema**, for bounded
  `INSERT ... SELECT` with unique explicit target columns, direct source
  projections and one `column = non-NULL literal` predicate. Supports
  direct-column `RETURNING`; other expressions, CTE/JOIN, ON CONFLICT,
  batch mutation and other providers remain fail-closed.
- Source and target both require independent physical metadata and catalog
  attestation (relation OID, column definitions/defaults, indexes and
  constraints). Only permanent, nonpartitioned, noninherited ordinary tables
  without RLS, user triggers or rewrite rules can receive execution proof.
- At commit, PostgreSQL SERIALIZABLE holds a source SHARE ROW EXCLUSIVE NOWAIT
  lock acquired **before its first catalog SELECT**, and a target ACCESS
  SHARE lock, then reattests both definitions and the entire approved
  projected source row multiset before one mutation. Lock conflicts, catalog
  drift and source-value drift reject or roll back. Source read permission
  is checked independently of target write permission before metadata reads.
- Real PostgreSQL tests exercise approved inserted RETURNING rows, replay
  denial, source update/index drift, row-limit rejection, locked concurrent
  writes and user-trigger rejection. Opt-in native schema freshness and
  adequate PostgreSQL table lock privileges are required. No new MCP tool
  or package; four read-only tools remain selected and DML remains off.

### G9 / M09 — SQLite INSERT SELECT direct RETURNING result rows

- Allow native SQLite **3.35+** bounded, single-statement `INSERT ... SELECT
  ... RETURNING` with distinct, unqualified direct target columns only
  (at most 32). Source/target may be the same or two separately attested
  real `main` tables; all earlier source-row approval proofs still apply.
- Preview binds complete selected source values, **not** generated target IDs.
  After revalidating both live schemas, source values and row count under the
  SQLite writer transaction, execute the compiled INSERT once and expose
  native RETURNING rows only following a confirmed commit. A source drift or
  returned-row count mismatch rolls back and returns no result rows.
- Real SQLite TDD covers commit, generated ID, approval replay, source drift
  and rejection of wildcard/expression/duplicate RETURNING items. The F#
  compiler still owns SQLite source/target version gating, with no new MCP
  tool, public API, provider support, driver or dependency. DML remains off
  by default.

### G9 / M09 — SQLite dual-physical-table INSERT SELECT approval

- Extend the previously bounded SQLite `INSERT SELECT` source-row approval
  to **one second, distinct, physical `main` source table**, while retaining
  existing same-table queries, INSERT VALUES and single-statement scope.
  Only explicit, unique target columns, direct unaliased source projections
  and one `column = non-NULL literal` source predicate are permitted.
- Bind **separate real source and target** metadata, complete SQLite table
  DDL, indexes and triggers into an ordered two-table schema digest.
  Both table definitions are independently re-attested in the held SQLite
  writer transaction before the full projected-value multiset and approved
  row count are rechecked; the insert still executes exactly once.
- Real SQLite regression tests cover approved cross-table insertion, source
  data drift, source-only DDL/index drift, over-limit source rows, unsafe
  aliases/expressions/OR and unchanged same-table approval.
- No general INSERT SELECT JOIN/CTE/RETURNING/UPSERT, attached database,
  multi-statement batch or cross-provider writes; no new MCP tool, driver
  or public API. The default four read-only tools and DML disabled remain
  unchanged.

### G9 / M09 — SQLite self-source INSERT SELECT approval

- Admit only explicitly bounded native SQLite `INSERT INTO main.table (columns) SELECT direct_columns FROM main.table WHERE column = literal`, with the source and target being the **same physical main table**. Preview hashes every projected source value, including duplicate rows; the approved row count and immutable compiler-generated SQL are bound into a one-time challenge.
- A real SQLite IMMEDIATE write transaction revalidates the physical schema and complete projected source row multiset before executing the approved native insert once. Changed source values, row count, schema, runtime database version or authorization context fail closed or roll back; no new MCP tool.
- Requires explicit `AddHsSqlAgentNativeDmlSchemaFreshness()`, a finite positive DML row limit and existing DML approval. No CTE, JOIN, aggregate, expression projection, RETURNING, UPSERT, independently unattested second source, attached database or atomic DML batch. Other five providers remain unchanged.

This is a **major-version release candidate**, not an already published tag. All first-party NuGet
packages use `VersionPrefix=3.0.0` in this branch. See the [documentation website](https://sql-agent.net/en/docs/).

### G9 / M08 — bounded SQL Server native multi-source DML

- SQL Server `WITH ... UPDATE ... FROM` and `WITH ... DELETE ... FROM`
  now admit **two separate native DML FROM sources**: a direct
  row-preserving CTE over a physically verified UNIQUE source, and
  exactly one additional, different, physically UNIQUE table. The
  complete 1–2 component original ordered key tuple must link
  target→CTE and CTE→second source by mandatory ordinary `AND`
  equalities. Each physical table has independent real enabled,
  unfiltered UNIQUE-index metadata and exact type/collation proof.
- Sealed F# execution admission proves both key equality sets.
  Host independently derives the physical second key tuple and
  uses the existing combined source+joined index assurance,
  including same-transaction `IsCurrent` and immutable row-set
  Preview→Approval. The independent static F# cardinality graph
  remains unchanged and fails closed without proven catalog facts.
- Real SQL Server UPDATE/DELETE integration tests verify one
  actually affected row out of three through a second-source filter;
  missing/swapped/computed/OR key comparisons, foreign second source,
  absent joined proof and index drift remain rejected.
- This is **not arbitrary multi-source UPDATE FROM/DELETE USING**,
  nor new PostgreSQL/other-provider mutation permissions. No new
  MCP tool, public API, driver, privilege or dependency. Four
  read-only tools remain selected by default and DML remains off.

### G9 / M07 — correlated semi/anti filters inside physical CTE diamond

- Extend the bounded SQL Server three-CTE, same-physical-source, two-column
  UNIQUE-key diamond to allow a whole-branch `EXISTS` or `NOT EXISTS`
  filter on either direct physical branch. The subquery must be over that
  same physical source with both original keys correlated by ordinary
  AND-connected equality; an additional direct inner field/literal equality
  can reduce the matched row set. The final JOIN and target must still match
  the complete ordered key.
- Independent F# static nonmultiplying cardinality proof, sealed F# live
  admission, Host physical-index discovery and held-transaction `IsCurrent`
  remain required. Real SQL Server UPDATE/DELETE validates exact 1-/2-row
  Preview→Approval→Commit and index revocation; negative cases reject
  partial/OR/computed/wrong-source correlation and extra joins.
- No general DAG, unrestricted subquery, new multi-source DML syntax, provider
  scope increase, MCP tool or public API expansion.

### G9 / M06 — bounded CTE diamond direct payload lineage

- A three-CTE SQL Server diamond may carry additional direct, injective
  payload projections beside its complete, ordered two-column physical UNIQUE
  key on **both source branches and the merged left-branch output**.
  Payload can be renamed and interleaved; per-CTE projection is limited to 32.
- Payload never grants uniqueness. The F# static cardinality proof and sealed
  F# execution gate independently keep all complete ordinary INNER key
  comparisons. The Host separately traces matched key outputs to the exact
  original physical UNIQUE index and insists that the full JOIN tuple matches.
- Real SQL Server UPDATE/DELETE, typed composite key, index drift, immutable
  Preview/Approval and same-transaction IsCurrent coverage; computed/duplicated
  columns, ambiguous aliases, CAST, right-branch merge output, missing keys
  or extra physical sources remain rejected. No other database or MCP default
  changes, and no new public API, driver, permissions or tools.

### G9 / M06 — bounded key-preserving CTE diamond

- Two independent SQL Server CTEs can project the same live UNIQUE source,
  reconverging through a third CTE's complete two-column INNER key JOIN.
  The direct projection still requires both target key equalities.
- F# static cardinality, F# sealed execution admission and Host live index
  discovery remain independent. Physical metadata, approval row-set and
  same-transaction `IsCurrent` remain mandatory.
- All other DAG shapes, extra physical sources, computed/cast keys,
  missing/duplicated JOIN edges, OR, LEFT JOIN, WHERE, aggregation and
  ambiguous aliases are fail-closed. No new tool, API or provider grants.

### G9 / M06 — two-level Derived Table physical key lineage

- Extend the previously proven SQL Server CTE UPDATE/DELETE source from
  one to **exactly two** nested, non-lateral, row-preserving derived
  projections over a single physically attested UNIQUE source table.
  Key aliases may change twice, but every ordered 1–2 component key must
  survive both layers and the existing 1–3 CTE hops unchanged in order.
- Retain three independent evidence gates: F# static cardinality, F#
  sealed physical-proof admission and Host real-index discovery with
  transaction-held `IsCurrent`. Neither CTE names nor Virtual Catalog
  metadata can mint executable permission. Existing rowset Approval and
  the six-provider fail-closed boundary are unchanged.
- Reject third-level nesting, LATERAL, computed/duplicate/omitted key,
  extra physical source, JOIN, WHERE, DISTINCT, GROUP, ORDER, LIMIT,
  set operations and arbitrary multi-branch CTE DAG joins. Tests include
  real SQL Server mixed-type UPDATE/DELETE, approval, index invalidation
  and negative shape matrices. No new MCP tool, public API or driver.

### G9 / M06 — bounded Derived Table key-preserving physical lineage

- Allow one non-lateral, row-preserving derived SELECT immediately under
  a SQL Server CTE root. Both derived and later 1–3 CTE projections may
  directly rename **complete** ordered physical unique key fields,
  including mixed numeric/decimal/UUID/attested-collation text kinds.
- Keep **two independent proof owners**: the existing F# mutation
  cardinality analyzer now shares one strict direct-column projection
  proof for derived and CTE nodes; Host live proof discovery traces
  aliases and column ordinals back to one physical source and still
  mints execution evidence only from real unfiltered UNIQUE index
  metadata, never from a virtual Catalog or a derived alias.
- Exclude multi-branch CTE DAG merging, recursive/nested/lateral
  sources, joins, filters, aggregates, DISTINCT, GROUP, set operations,
  ORDER, LIMIT/OFFSET/TOP, computed keys, alias duplication, dropped
  UNIQUE components and ambiguous lineage. Non-multiplying extra
  physical joins remain governed by their existing separate M07 gate.
- Retain sealed source+target physical proof, mandatory AND/equality,
  Preview/Approval, current schema digest, rowset identity and held
  transaction `IsCurrent` to block index revocation after preview;
  no change in proof fingerprint structure or extra approval version.
- Real SQL Server 2022 RED→GREEN matrix covers bounded derived
  UPDATE/DELETE, 1–3 CTE hops, mixed key kinds, Host auto-proof,
  approved commit and index invalidation, plus fail-closed negatives.
  Other five database providers and existing DML defaults unchanged;
  zero new public API, tool, dependency or driver.

### G9 / M05 — exact SQL Server linguistic-collation equality

- Extend the closed F# physical KeyTuple with private
  `SqlServerTextEquality` cases for `Latin1_General_100_CI_AS`,
  `Latin1_General_100_CI_AI` and `Latin1_General_100_CS_AS`;
  retain the prior `Latin1_General_100_BIN2` semantics. The actual
  **comparison class is embedded in each private VARCHAR/NVARCHAR kind**,
  not inferred from generic `Text` or a caller-provided collation name.
- Source UNIQUE index, target and independently proven second JOIN
  must have exactly matching native type, byte width, entire ordered
  key tuple and physical column collation. No implicit text coercion
  or source/target collation override may mint evidence; all other
  collations, alias or MAX types, filtered/disabled/nonunique indexes,
  computed keys and unproven JOINs remain denied.
- Preserve nullable-UNIQUE `ANSI_NULLS ON` witness, full metadata
  digest, immutable approval, independent rowset Preview and commit
  under transaction-held `IsCurrent`. Advance ProofIdentity to
  **v9**, invalidating v8 pending approvals.
- Add actual SQL Server 2022 **UNIQUE conflict-versus-native equality
  parity** across case and accent distinctions (not just grammar
  acceptance), and real UPDATE/DELETE, 1–3 CTE, composite mixed text,
  numeric, UUID and secondary-table JOIN approval/DDL-drift matrices.
  Six-database scripts and compiler benchmark parity stay gated.
- **Unreleased, develop only**. No new public API, dependency,
  MCP tool, parser, driver, default DML/DDL selection or permission.

### G9 / M05 — Nullable SQL Server source UNIQUE + ordinary equality witness

- Introduce the **closed F# source-null comparison witness**
  `AllKeysNotNull | NullableKeysOrdinaryAnsiEquality`; a nullable
  source UNIQUE index can now support the existing bounded CTE
  mutation only if a real 1–2 key enabled, unfiltered, built-in-type
  UNIQUE index exists, there is a mandatory ordinary AND equality
  graph to nonnullable composite target keys, and the actual live
  session is `ANSI_NULLS ON`. NULL-containing source tuples
  cannot match any target under that ordinary equality.
- Reject `VerifyLive` without an equality target, filtered
  `IS NOT NULL` indexes, nonunique/disabled/hypothetical keys,
  partial/OR/COALESCE/CAST/NULL-safe equality and virtual Catalog
  uniqueness. The source and target type/collation/width checks and
  full ordered index key metadata remain mandatory.
- Check `@@OPTIONS` when minting nullable source proof **and**
  within transaction-held `IsCurrent` before the actual DML;
  retain immutable schema/rowset/approval fingerprint and
  index/column nullability digest. ProofIdentity advances to **v8**
  so an approved v7 plan cannot be replayed.
- Real SQL Server 2022 RED→GREEN proves nullable source BIGINT,
  DECIMAL, UUID and BIN2 VARCHAR composite tuples, real NULL
  nonmatches, UPDATE/DELETE 1–3 CTE/Approval/Commit, revoked
  physical index and filtered/disabled/nonunique negatives.
  Six other existing query/DDL semantics and all other five
  database engines remain unchanged; no added MCP tool,
  driver, public API, permission or default DML selection.

### G9 / M04 — exact BIN2 VARCHAR/NVARCHAR physical key proof

- Extend the **same** private closed F# SQL Server KeyTuple with
  `VarChar(n) | NVarChar(n)` under the **single attested collation**
  `Latin1_General_100_BIN2`; require matching native family and
  maximum byte length at every source/target/independent JOIN index
  ordinal. A typed Text snapshot is not execution authority; real
  `sys.columns.max_length`, `collation_name`, built-in `sys.types`
  identity and complete enabled/unfiltered UNIQUE index are mandatory.
  MAX, CHAR/NCHAR, different BIN2 or case-insensitive collations,
  width or Unicode coercions, user aliases and nullable source keys
  continue fail-closed.
- Keep the existing mandatory ordinary key equality graph, bounded
  1–3 CTE layers, singleton/self/physical JOIN, Preview/Approval
  fingerprint, audit and same-transaction locked `IsCurrent`.
  ProofIdentity advances to v7 to invalidate v6 pending approvals
  and bind the exact string type/length to the physical digest.
- RED→GREEN real SQL Server 2022 matrix covers UPDATE/DELETE,
  composite mixed text/UUID/decimal/integer keys, secondary physical
  JOIN, Host discovery, approved commit and index revocation, plus
  binary collation, length and source/target snapshot negative cases.
  Six provider compiler/baseline gates remain mandatory.
- No new CLR API, MCP tool, SQL runner, permission, or default DML
  selection; `develop` only, not a production release.

### G9 / M04 — SQL Server exact UNIQUEIDENTIFIER physical keys

- Add a closed F# `Uuid` physical key kind with exact live
  `sys.columns`/`sys.types` identity and unchanged full source UNIQUE
  index provenance. SQL Server `UNIQUEIDENTIFIER` is not implicitly
  `Numeric`, `Text` or `Binary`; no GUID string coercion, alias type
  or unsafe CAST may mint a proof.
- Add explicit portable immutable `SqlSchemaValueKind.Uuid` (append-only
  enum value) and bounded Host physical typed-key snapshot discovery for
  source, target and independently attested second JOIN source. The F#
  live proof requires matching Snapshot and physical source/target kind
  on **every** key ordinal, never trusting virtual Catalog metadata as
  execution authority. Existing integer/DECIMAL keys stay supported.
- Keep 1–3 CTE projection, full AND key equality, self/singleton/second
  physical JOIN, bounded result-row Preview, independent Approval,
  held schema locks and same-transaction `IsCurrent`. ProofIdentity
  advances to v6, preventing v5 approval replay against new key shapes.
- SQL Server 2022 tests-first matrix verifies real UUID UPDATE/DELETE,
  mixed physical composite keys, Host-discovered proof, approved commit,
  revoked UNIQUE indexes and UUID-vs-integer/text/binary rejection.
  **Unreleased develop-only**; no new tool, provider authorization,
  compiler, driver or default DML/DDL selection.

### G9 / M04 — exact SQL Server fixed-decimal physical keys

- Extend the same private F# closed physical KeyTuple to
  `FixedDecimal(precision, scale)` for built-in SQL Server `DECIMAL(p,s)`
  and `NUMERIC(p,s)` (SQL synonyms), with `1 ≤ p ≤ 38`, `0 ≤ s ≤ p`.
  The source UNIQUE index, target comparison and any independently
  attested secondary JOIN index must agree at **every exact physical
  precision and scale**. A generic Catalog `Numeric` classification,
  implicit decimal rounding/widening, user-defined alias or mixed
  DECIMAL/INT equality cannot confer proof.
- Retain every physical index key ordinal before examining its type;
  this closes a potential false one-key uniqueness inference from a
  composite index with unsupported user-alias component. Preserve
  live metadata digest, transaction-held schema locks, Preview,
  row-set identity, independent Approval and `IsCurrent` re-attestation.
  Version immutable ProofIdentity to v5 so v4 approval evidence
  cannot authorize expanded decimal-key plans.
- SQL Server 2022 TDD covers 1–3 CTE hops, UPDATE/DELETE,
  fractional values, mixed DECIMAL/NUMERIC and integer key tuples,
  singleton/self/dual-physical JOINs, correct source/target identity,
  revoked physical index and negative precision/scale/cast/alias-index
  cases. Existing 4 built-in integer kinds remain fully supported.
- Backend and seven-locale docs **develop** only, no main/release,
  MCP tool, driver, public API or new permissions. UUID, text, nullable
  UNIQUE and arbitrary multi-join still fail closed.

### G9 / M04 — exact 8/16-bit physical key kinds

- Extend SQL Server's private, closed physical KeyTuple algebra from
  `INT` / `BIGINT` to **built-in `TINYINT` (unsigned 8-bit) and
  `SMALLINT` (signed 16-bit)**. Each source UNIQUE index, target
  comparison column and independently attested JOIN key must match
  its corresponding physical SQL type exactly; conversions and alias
  user types cannot mint a proof.
- Preserve live `sys.*` index ordinal/type/identity evidence, bounded
  CTE lineage, singleton/self/second-table JOIN proof, row-set
  preview, independent approval and same-transaction `IsCurrent` recheck.
  The exact proof identity advances to v4 to prevent older approval
  evidence from being replayed across this expanded physical type model.
- SQL Server 2022 RED→GREEN coverage adds UPDATE/DELETE, primary/
  unique keys, mixed SMALLINT/TINYINT/INT/BIGINT ordinals, 1–3 CTE
  hops, direct payload, preview→approval→commit, post-preview index
  revocation, independent second-table JOIN and wrong-type denial.
- **Unreleased**: No new MCP tool, driver, permissions or other
  provider semantics. DECIMAL, UUID, nullable source UNIQUE and
  unproven conversion/JOIN shapes remain fail-closed.

### M07 — correlated semi-/anti-join filters on one attested source

- Allow **EXISTS/NOT EXISTS** as a first-CTE WHERE filter in native SQL
  Server UPDATE/DELETE, limited to a read-only, nonmultiplying correlated
  query on the **same physically live-attested source table**.
- Require every ordered INT/BIGINT physical source UNIQUE key component
  to be compared to its matching outer alias by ordinary mandatory
  `AND`/`=` predicates; optional direct inner column/literal equality
  predicates can further restrict rows. No additional table, JOIN,
  aggregation, recursion, set branch or computed/OR key equality.
- The CLR Host and closed F# AST independently validate the bounded
  predicate; continue to bind the source unique-index metadata,
  snapshot, row-set preview and approval fingerprint to the **same
  transaction's** `IsCurrent` check. An index lost after approval
  rejects the write; `NOT EXISTS` cannot expand the source rowset.
- Real SQL Server 2022 tests cover 1–3 CTE hops, EXISTS and NOT EXISTS,
  UPDATE/DELETE, mixed INT/BIGINT composite keys, rejected partial/OR/
  computed/ambiguous correlation and approval-time index drift.
- **Unreleased** on backend/site develop, with no default permission,
  new MCP tool or arbitrary EXISTS/anti-join privilege.

### M07 — independently attested second physical JOIN source

- Extend SQL Server native CTE `UPDATE`/`DELETE` to one bounded
  `INNER` or `LEFT JOIN` against a **different** physical source table.
  Each source independently requires a live enabled/unfiltered UNIQUE
  `INT`/`BIGINT` key of one or two exact, nonnullable components;
  the JOIN must equate every component in order without casts or `OR`.
- Bind the second physical index/object, source types, database identity
  and metadata digest into the original execution/approval proof, and
  reattest **both** proofs on the actual commit transaction.
  The closed F# compiler independently proves the complete JOIN
  equality graph. No virtual catalog can mint authority.
- Real SQL Server 2022 tests cover UPDATE/DELETE, INNER/LEFT, CTE
  chains, mixed-width keys, after-preview secondary-index revocation
  and missing/disabled/filtered/nullable/nonunique/wrong-type/reversed
  secondary index cases. No new MCP tools, new SQL providers or
  general multi-table cardinality authorization.
- **Unreleased:** main product/site branches remain untouched; site
  documentation is developed and verified on its own `develop`.

### G9 / M04 — SQL Server exact integer key equivalence

- Support a bounded live-verified, **exact INT/BIGINT** physical source-key
  and target equality pair at each ordinal of one-/two-component SQL Server
  CTE UPDATE/DELETE unique-key proof; mixed `(INT, BIGINT)` ordinals are
  allowed only where the actual source and target column types match.
- Reuse existing Host discovery, closed F# CTE and JOIN AST proof,
  strict row-set Preview/Approval, immutable identity and commit-time
  transaction-held metadata recheck. A private closed F# integer-kind DU
  binds each source/target physical key ordinal; versioned fingerprint
  prevents legacy single-INT approval replay.
- Real SQL Server 2022 tests include values above signed 32-bit maximum,
  PRIMARY KEY and UNIQUE index, 1–3 CTE hops, UPDATE/DELETE, Host approval,
  stale-index recheck and negative `INT`↔`BIGINT` implicit coercions,
  `SMALLINT`/`DECIMAL`/`FLOAT`/`UUID` and computed/nullable keys.
- **Unreleased:** backend `develop` only; documentation stays on site
  `develop`. The production site `main` remains pinned to the last
  published release. No new MCP tool, driver or permission inheritance.

### SQL Core G1/G2, PostgreSQL G3, SQLite/SQL Server/Firebird G4, MySQL/Oracle G5 and PostgreSQL G6 DDL

- Introduce a separate closed F# `CompileDdl` / `TryCompileDdl` compiler,
  typed Schema Plan, native six-dialect SQL renderer, deterministic fingerprint,
  capability checks and fail-closed limited additive DDL grammar.
- Introduce `PreviewDdl` / `TryPreviewDdl` for immutable hypothetical
  Before/After Schema Diff, physical preconditions, dependency impacts, risks
  and explicit unknown states; virtual Catalog is not live authorization.
- Add opt-in **PostgreSQL** `execute_ddl_sql` with read-only preview,
  independent access-key permission and DDL Approval, physical database/cluster
  and schema identity, transactional recheck, native-compiled DDL execution,
  post-execution verification, fail-closed audit and OutcomeUnknown.
- No executable DDL feature is enabled by default. Four existing read tools
  remain selected; both DML and DDL are unselected. No cross-provider,
  conditional or concurrent index DDL execution. A separately enabled,
  independently approved PostgreSQL G6 empty-table DROP is the sole destructive
  exception. PostgreSQL G6/S16 adds only a 2–4 independent CREATE TABLE batch
  under a distinct default-disabled permission and atomic-transaction proof.
- Real PostgreSQL 16 tests cover approved CREATE TABLE / ADD COLUMN / INDEX,
  Preview and rejected Approval without writes, schema drift and forged
  approval fingerprints. Six-provider DQL/DML compatibility gates remain
  mandatory.
- G4 SQLite supports an independent opt-in file-backed transactional DDL adapter
  with main-schema identity and schema-cookie freshness proof.
- G4 SQL Server 2022 adds an independent, default-disabled CREATE TABLE-only Host
  path with explicit schema allowlist, bounded simple INT columns, separate
  DDL approval, SQL Server physical metadata proof, transaction recheck and
  OutcomeUnknown handling. Neither Host adapter inherits DML permissions.
  SQL Server ALTER/INDEX and unsupported DDL operations remain
  disabled pending separate live safety proofs.
- G4 Firebird 5 introduces a separately opt-in, one-table CREATE TABLE Host
  boundary: allowlisted live database GUID/file identity, simple INTEGER
  columns, no active database/DDL triggers, independent DDL approval,
  transaction recheck, native SqlCore SQL only, typed postcondition, explicit
  rollback and OutcomeUnknown. ALTER/INDEX and destructive Firebird DDL remain
  disabled. No default MCP tool selection or DML approval policy changed.

- G5 MySQL 8.4 LTS adds an independently approved, default-disabled,
  one-statement InnoDB CREATE TABLE Host. Requires an explicitly allowed
  physical database, verified MySQL UUID/version/case mode and InnoDB
  default, live table absence, post-execution exact column metadata,
  and a session-level cooperative DDL lock. There is NO user rollback:
  CREATE TABLE implicitly commits, and lost native execution replies are
  OutcomeUnknown and never retried automatically.
  ALTER/INDEX/DROP/CTAS/TEMPORARY remain MySQL Host-disabled.

- G5 Oracle 26ai adds independent, opt-in, own-schema, single-statement
  CREATE TABLE for 1–16 INTEGER columns. Live PDB DBID/GUID, database role,
  schema owner, engine version and DBA_TRIGGERS access are mandatory.
  A cooperating session lock, fresh metadata and separate DDL approval
  guard one F#-compiled command. Oracle implicitly commits DDL: no
  synthetic rollback; lost result/invalid postcondition is OutcomeUnknown.
  No new MCP tool, F# compiler, database driver or default tool grants.

### G6 S15: PostgreSQL high-risk empty-table DROP

- Extend the same closed F# Schema Compiler with typed, qualified
  `DROP TABLE schema.table RESTRICT` only for PostgreSQL. The plan explicitly
  records destructive risk, potential data loss and a physical emptiness
  precondition. Virtual Catalog Preview remains hypothetical.
- Add `AllowDropEmptyTable` (**false by default**) and a separate
  `AllowedDropPrincipals` MCP-key identity allowlist (**empty by default**)
  to the PostgreSQL Host. This requires the existing separately granted
  `execute_ddl_sql` permission, independent human DDL approval, immutable physical evidence,
  policy revision and short-lived approval challenge.
- Enforce an owned, ordinary, persistent, non-partitioned, non-replicated,
  RLS-free and privilege-safe table with 1–16 INTEGER columns and **zero
  rows**. Reject indexes, constraints, view dependencies, triggers,
  identity/default columns, inheritance, owned sequences and other
  unproven effects. Recheck all requirements under PostgreSQL
  ACCESS EXCLUSIVE transaction lock before emitting one compiled native DROP.
- Confirm physical absence before COMMIT; use proven rollback on failure
  and `OutcomeUnknown` when COMMIT or rollback cannot be proven. Nothing
  permits dropping data-bearing tables, using CASCADE, other providers,
  TRUNCATE, DROP COLUMN, or multi-DDL batches.
- PostgreSQL 16 live tests cover preview/no-write, explicit opt-in,
  rejected/forged approval, nonempty and concurrent inserts, dependent
  views, exact absence and independent transactional rollback.

### G6 S16: PostgreSQL atomic CREATE-only DDL Batch

- Introduce an independent, **default-denied** PostgreSQL 16 whole-batch
  execution boundary on the existing `execute_ddl_sql` tool. A caller must
  explicitly opt into `AllowCreateTableBatch`, list the authenticated MCP
  key under `AllowedBatchPrincipals`, grant the existing DDL tool, and use
  the independent `IDdlApprovalProvider`.
- Reuse the F# Script lexer and single-statement Schema Compiler to compile
  **2–4 independent schema-qualified CREATE TABLE statements** into one
  ordered immutable plan fingerprint. Preview remains non-authoritative and
  does not write. Exactly one approval binds every native SQL command,
  source order, database/cluster/schema identity, fresh physical absence,
  principal, policy revision, expiration and a one-use request nonce.
- A fresh PostgreSQL transaction takes cooperative advisory locks in
  deterministic order, rechecks all physical preconditions, runs only
  closed compiled commands and verifies every typed postcondition before
  COMMIT. Confirmed rollback prevents partial application. Uncertain COMMIT
  or rollback is `outcome_unknown` and is never automatically retried.
- Cross-statement dependencies, ALTER, DROP, mixing query/DML/transaction
  statements, five other provider batches and MySQL/Oracle implicit-commit
  partial-outcome modeling remain **unsupported and rejected**. This is
  a proven transactional subset, **not** general multi-DDL execution.

### G6/S16 continuation: PostgreSQL dependent CREATE → ADD → INDEX batch

- Extend only the PostgreSQL 16 atomic Batch Host with a **separately
  default-disabled** `AllowDependentDdlBatch` gate, in addition to the
  existing independent `AllowCreateTableBatch`, `AllowAddColumn`,
  `AllowCreateIndex`, exact MCP principal allowlist and human approval.
- The F# Batch Compiler reuses the shared Script SourceSpans and closed
  single-DDL Compiler to construct one of two **typed and fingerprinted**
  topologies: previously supported unrelated CREATE-only batch, or a
  *single newly created table* with one/two nullable ADD COLUMN effects
  and an optional final ordinary CREATE INDEX referencing only materialized
  column names. No duplicate column, existing table modification,
  cross-target/interleaved CREATE, or unsupported DDL operations.
- Preview retains only **real physical Before snapshots** as evidence;
  cumulative intermediate Before/After are explicitly hypothetical effects
  and must never be used to skip metadata checks. After one human approval,
  each staged operation requires fresh real PostgreSQL metadata preconditions
  and exact postconditions inside the same transaction, with whole-table
  final readback before COMMIT and fail-closed rollback or OutcomeUnknown.
- Tests: positive and adversarial grammar matrix; real PG16 preview/no-write,
  independent approval, default-denied permission, external table drift,
  index namespace collision, final column/index parity and transactional
  rollback. Existing five-other-provider Batch requests stay rejected.

### G4: SQLite native DDL and live transactional Host adapter

- Fix the SQLite F# native `CREATE INDEX` renderer to place the `main`
  qualifier on the index name, not the `ON` table (SQLite rejects
  `CREATE INDEX idx ON main.table`). Validate against a real SQLite engine.
- Add a separate, default-disabled, file-backed SQLite DDL Host adapter
  reusing the existing `execute_ddl_sql` tool and independent
  `IDdlApprovalProvider`. No additional MCP tools or inherited DML consent.
- Enforce actual `main` file identity, schema cookie/physical SQL snapshot,
  SQLite ASCII-NOCASE identifiers, BEGIN IMMEDIATE transaction freshness,
  exact typed postconditions, explicit rollback and OutcomeUnknown reporting.
- Exercise live CREATE TABLE, nullable ADD COLUMN, plain CREATE INDEX,
  Preview, reject/forged approval, schema drift, case-conflict, and native
  transaction rollback. SQL Server/Firebird DDL execution remain disabled.

## [3.0.0] - 2026-10-25

Published as product tag `v3.0.0` on 2026-10-25; first-party NuGet packages used `VersionPrefix=3.0.0`. The tagged release-candidate checklist below is retained as historical evidence, not outstanding work for v3.1.0.

### PostgreSQL old-worker cutover verification

- Introduce a fail-closed, read-only PostgreSQL old-role verifier that
  checks effective table DML rights, transitive NOINHERIT/SET ROLE membership,
  surviving sessions and privileged monitoring authority. Live database
  tests cover grants, revocation, session drain and incomplete evidence.
  This does not attest other SQL providers or production topology.

### SQL Server old-login cutover verifier

- Add an independent read-only SQL Server old-login privilege/session verifier
  requiring full inspection authority, a mapped database user, effective
  table/view/column DML checks and session-drain evidence. The SQL Server
  mandatory real database job verifies direct grants, inherited
  `db_datawriter` access, grant removal, rejected incomplete inspection and
  live old login sessions. No automatic privilege revocation is performed,
  and SQL Server procedural/ownership-chain privileges require separate audit.

### Breaking changes and required migration

- Third-party `IDmlApprovalChallengeStore` implementations must now implement atomic
  `TryPromoteAsync`; this is intentionally source-breaking. A single-statement MCP DML
  operation also uses the aggregate challenge pipeline.
- Redis approval keys now use a `{dml-approval}:` hash-slot segment, and
  challenge values use an explicit `SchemaVersion=1` payload envelope.
  Lua promotion compares these exact versioned bytes. Legacy v2 keys
  are not consumed by v3; **drain old workers and outstanding approvals before cutover**.
  Real-Redis integration tests prevent accidental changes to this contract.
- Approval challenges without the new plan binding are rejected. Re-preview and
  obtain new human approval; do not replay consumed approvals.
- Introduce a versioned encrypted durable DML resume payload with an explicit
  legacy v2 decoder. Unknown versions fail closed instead of silently defaulting
  new approval safety requirements; the protected-payload column remains unchanged.
- Add database-enforced `AdmissionProtocolVersion` to both Admin Store providers
  so v2 binaries omitting the field (default 0) cannot insert new Pending/
  Executing approvals after v3 migration. Historical terminal evidence
  stays intact, and new versioned durable requests explicitly write 1.
- Under selective UPDATE/DELETE policy, demonstrably always-true predicates,
  including `WHERE 1=1`, are denied unless full-table authority is explicit.
- Compiler input/token resource ceilings reject previously accepted oversized SQL.

### SQL compiler, DML integrity and reliability

- Add opt-in schema-aware name/type proofing and six-provider native DML schema
  attestation with commit-time freshness checks on the same database transaction.
- Bind approved SQL plan shape, per-statement row-set evidence and authorization
  into immutable one-time approval challenges, with atomic multi-statement DML.
- Preserve distinct uncertain COMMIT versus ROLLBACK outcomes throughout durable
  completion and synchronous MCP structured responses. Both are non-retryable.
- Add experimental, opt-in target-enlisted COMMIT witnesses and execution fencing;
  this is not a claim of fleet-wide safe revocation or automatic `Executing` reconciliation.
- Expand positive/negative grammar regressions, real-provider transaction tests,
  Redis integration and fail-closed recovery test coverage.
- Provide a public rollback-only target-fence readiness probe with real
  six-provider transaction tests, including physical absent-witness and
  absent-unique-key failures plus injected witness-error rollback. A table
  with no actual `REQUEST_ID` uniqueness cannot pass readiness; additionally
  reject misleading (REQUEST_ID, ENTRY_KIND) and fingerprint-only unique keys
  and reject generic database errors as uniqueness proof. Document
  provider-specific legacy credential cutover requirements; this does
  not automatically prove old-worker quiescence.
- Integrate the built-in physical probe into **every fenced single/batch
  target DML submission**, requiring the actual connection to prove schema,
  uniqueness and rollback before approved mutations. This has two extra
  rolled-back SERIALIZABLE transactions per opted-in commit, with no
  cached or self-attested readiness.
- Verify the target-enlisted fence's revocation/admission competition,
  COMMIT-witness atomicity and rollback behavior in actual PostgreSQL, MySQL,
  SQLite, SQL Server, Oracle and Firebird integration fixtures. Reject legacy
  Pending approvals on fence-enabled workers instead of downgrading execution.

### Release gates

- Verify full CI and package smoke with the unified `3.0.0` version.
- Finish the v2-to-v3 drain / no-mixed-worker migration verification.
- Document experimental fence limits: passing six-provider database transaction
  tests is **not** proof of mixed-version fleet enrollment, an independently
  authorized revocation operator or safe `Executing` reconciliation.
- Docker Compose still pins the last published 2.x image in this **unreleased**
  branch; retag release assets and deployment examples only when the v3 image
  has actually been published.

The following administration and upgrade clarifications were carried forward from the previous `develop` draft. They describe earlier shipped behavior, **not** new v3.1.0 capabilities.

### Management API Keys shipped by v3.0.0 (historical clarification)

- Add dedicated **Management API Keys** for machine-to-machine access to the existing `/api` management endpoints. Authenticate using `X-API-Key: hsa_mgmt_...`; human sessions continue to use `Authorization: Bearer <JWT>`.
- Provide an Admin UI for issuing, listing, immediately revoking, and rotating Management API Keys, with one-time plaintext display and an explicit allowlist of canonical API permissions.
- Keep Management API Keys **fully separate** from MCP access keys, MCP tool authorization, and SQL execution. Machine keys cannot operate built-in identity/role administration, edit security policy, or issue other management keys.
- Persist hashed credentials with expiry / usage metadata in the Admin Store; add **SQLite and PostgreSQL schema migrations**, plus permission and credential-isolation tests.
- Remove the repository docs directory; the documentation website already provides project documentation.

### v3.0.0 upgrade notes (historical clarification)

- Upgrading to 3.0.0 requires the Admin Store migrations for `ManagementApiKeys` before creating keys.
- Existing JWT admin authentication and MCP keys are unchanged. No new API key is issued automatically.
- Management API Keys are currently scoped by canonical API permissions, **not** by database or SaaS tenant; protect these keys as server-to-server credentials.
- Follow the versioned documentation site's Management API Key guide for the request header and examples.

## [2.0.6] - Unreleased (proposed)

### Programmatic administration

- Add dedicated **Management API Keys** for machine-to-machine access to the existing `/api` management endpoints. Authenticate using `X-API-Key: hsa_mgmt_...`; human sessions continue to use `Authorization: Bearer <JWT>`.
- Provide an Admin UI for issuing, listing, immediately revoking, and rotating Management API Keys, with one-time plaintext display and an explicit allowlist of canonical API permissions.
- Keep Management API Keys **fully separate** from MCP access keys, MCP tool authorization, and SQL execution. Machine keys cannot operate built-in identity/role administration, edit security policy, or issue other management keys.
- Persist hashed credentials with expiry / usage metadata in the Admin Store; add **SQLite and PostgreSQL schema migrations**, plus permission and credential-isolation tests.
- Move in-depth operator and integration guides to the standalone documentation site; `docs/*.md` in the application repository is no longer a documentation home.

### Upgrade notes

- Applying 2.0.6 requires the Admin Store migrations for `ManagementApiKeys` before creating keys.
- Existing JWT admin authentication and MCP keys are unchanged. No new API key is issued automatically.
- Management API Keys are currently scoped by canonical API permissions, **not** by database or SaaS tenant; protect these keys as server-to-server credentials.
- Follow the versioned documentation site's Management API Key guide for the request header and examples.

## [2.0.5] - Unreleased

### Agent-facing MCP contracts

- Added structured MCP output contracts for all five built-in tools while preserving JSON text fallback compatibility for older MCP clients.
- `execute_query_sql` now returns typed rows, provider, row count, duration, and machine-readable error details instead of requiring clients to parse free-form strings.
- `get_schemas`, `get_tables`, and `get_columns` now expose machine-readable schema, table, column, semantic metric, relationship, and metadata structures.
- `execute_dml_sql` now models `committed`, `pending`, `rejected`, and `failed` outcomes explicitly, including approval request/external references, affected rows, returned rows, and structured failures.
- Read/query tools are explicitly marked read-only; the safe MCP key default remains four read/query tools selected with DML opt-in.

### SQL Explain and policy simulation

- Added a compile-only SQL Explain / Policy Simulator backed by the real F# compiler pipeline and the selected database runtime capability profile.
- Query and DML simulations expose rendered target SQL, parameters, referenced tables, compiler decision/boundary, diagnostics with source spans, source/target capability evidence, and policy evidence.
- Simulations can apply an existing MCP key's tool and table scopes without executing SQL, opening transactions, creating approval requests, or mutating target data.
- DML affected-row limits remain runtime-preview evidence and are not misrepresented as compile-time proof.
- Added a Security-page Admin UI for interactive explain/simulation with explicit compile-only labeling.

### Configuration Doctor and deployment readiness

- Added Configuration Doctor under Operability with `Healthy`, `Warning`, and `Error` findings plus concrete remediation guidance.
- Doctor classifies coordination posture as `SingleNode`, `Distributed`, or `Mixed`, checks Redis completeness and fail-closed distributed limiter/concurrency settings, and diagnoses MCP endpoint, Admin Store, Data Protection, DML webhook, OIDC, OTLP, Alert/SIEM, and secret-placeholder posture without returning secret values.
- Expanded Home System Readiness into a five-stage launch path: configuration blockers, database, public MCP endpoint, MCP key, and first governed agent request.
- Doctor warnings remain reviewable while Doctor errors are treated as launch blockers; onboarding may display the public endpoint but never MCP key secrets.

### Release and compatibility

- Set the first-party package version to `2.0.5` and updated NuGet release metadata for the Agent DX, explainability, and readiness release.
- Existing fail-closed SQL compiler behavior, capability checks, table/tool authorization, DML approval/revalidation/rollback, server-owned transaction boundaries, and Admin Store schema remain unchanged.
- No Admin Store schema migration is required for this release.

## [2.0.4] - 2026-09-07

### MCP contract and security

- Added one canonical server-side catalog for the five documented built-in MCP tools: `get_schemas`, `get_tables`, `get_columns`, `execute_query_sql`, and `execute_dml_sql`.
- MCP startup now fails closed when reflected built-in tools do not exactly match the canonical catalog, preventing accidental tool-surface expansion.
- Removed the undocumented `update_semantic_layer` method from MCP discovery. The CLR method remains for compatibility, while Semantic Layer writes continue through the permission-protected Admin UI/API.
- The Admin tool-catalog endpoint now returns built-in/custom classification, Query/DML type, display name, and risk metadata for MCP-key experiences.
- Added a regression test that locks the public built-in MCP method surface to the five-tool contract.

### Developer experience

- Set the next first-party package version to `2.0.4`.
- Aligned the frontend Docker build with CI/package-manager pins: Node.js 22, pnpm 10.22.0, and `pnpm install --frozen-lockfile`.

### Admin UI

- Removed the inert `Search the docs...` sidebar control.
- Replaced template-like sidebar branding with `hs-sql-agent` / `Admin Console`.

### Compatibility note

`update_semantic_layer` was not part of the documented 2.0.x five-tool MCP contract. Deployments that called it through MCP should move semantic metadata writes to **Runtime → DB Management → Semantic Layer** or the Admin API before upgrading. Existing MCP keys, SQL compiler behavior, DML approval semantics, and Admin Store schema do not require migration for these changes.

## [1.8.3] - 2026-06-26
## What's Changed
* Remove outdated documentation files: Architecture, Configuration, Dep… by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/59
* Add cover image to README by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/61
* upgrade package by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/62
* fux Vulnerabilities by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/63


**Full Changelog**: https://github.com/tse-wei-chen/hs-sql-agent/compare/v1.8.2-alpha...v1.8.3

## [1.8.2-alpha] - 2026-06-02
## What's Changed
* fix: add missing Dapper package reference causing runtime assembly load failure by @Copilot in https://github.com/tse-wei-chen/hs-sql-agent/pull/54
* fix: move assignment of issuedPlaintextKey to after form reset for co… by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/56
* fix: error 500 for connect to mcp endpoint by @tse-wei-chen in https://github.com/tse-wei-chen/hs-sql-agent/pull/58

## New Contributors
* @Copilot made their first contribution in https://github.com/tse-wei-chen/hs-sql-agent/pull/54

**Full Changelog**: https://github.com/tse-wei-chen/hs-sql-agent/compare/v1.8.1-alpha...v1.8.2-alpha

## [1.8.1-alpha] - 2026-05-30

### NuGet Package Release
fix package use

## [1.8.0-alpha] - 2026-05-30

### NuGet Package Release

`HsSqlAgent.Server` is now available as a NuGet package — embed the full MCP SQL Agent into any existing ASP.NET Core application.

```bash
dotnet add package HsSqlAgent.Server
```

```csharp
builder.Services.AddHsSqlAgent(options => { ... });
app.UseHsSqlAgent();
// app.UseHsSqlAgent().ServeAdminUi();  // optional Admin UI
```

- **Admin UI embedded in DLL**: The frontend SPA is compiled and embedded as assembly resources (`EmbeddedFileProvider`). No external `wwwroot` files to deploy — the UI ships inside the NuGet package.
- **Dual-mode pipeline**: `UseHsSqlAgent()` for API-only (MCP + Admin API), `.ServeAdminUi()` to also serve the built-in Admin UI.
- **Project references included**: `Admin.Service`, `Common`, `SqlAgent.Service`, and `SqlKata.*` are automatically bundled in the package via `CopyProjectReferencesToPackage`.
- **GitHub Actions workflow**: New `.github/workflows/nuget-publish.yml` to publish on release or manually via `workflow_dispatch`.

### Docker

- **Fixed duplicate frontend build**: The `CompileFrontend` MSBuild target now skips `pnpm install/generate` when `wwwroot/index.html` already exists (e.g., Docker pre-populated). The existing files are still embedded as resources.
- **Frontend dist goes to HsSqlAgent.Server**: Dockerfile copies frontend output to `HsSqlAgent.Server/wwwroot/` instead of `ToolBox/wwwroot/`, aligning with the embedded resource strategy.

### Infrastructure

- Added `Microsoft.Extensions.FileProviders.Embedded` package reference.
- Added `<IsPackable>true</IsPackable>` to `HsSqlAgent.Server.csproj`.
- Added `NODE_ENV=production` to the `pnpm generate` step in `CompileFrontend`.
- Documentation moved to [GitHub Wiki](https://github.com/tse-wei-chen/hs-sql-agent/wiki) — main README simplified with links.

## [1.7.0-alpha] - 2026-05-27

### Breaking Change

**Custom Tools saved with the old query definition schema will fail to execute.** Existing saved query definitions (in `CustomTool.JsonDefinition`) that use the removed types `"type": "in"`, `SelectArithmeticCondition`, or `SqlFunctionArgument` can no longer be deserialized. Affected Custom Tools must be rebuilt or their JSON definition updated to the new schema.

- **Removed `InWhereCondition`**: IN/NOT IN is now handled by `BasicWhereCondition` via `Operator = "IN"` + `Values` array. The `type: "in"` discriminator no longer exists.
- **Removed `SelectArithmeticCondition`** (5 types: `FieldArithmeticCondition`, `ConstantArithmeticCondition`, `OperationArithmeticCondition`, `FunctionArithmeticCondition`, `CaseWhenArithmeticCondition`): Arithmetic operands now use the unified `SelectCondition` type directly, eliminating the redundant parallel hierarchy.
- **Removed `SqlFunctionArgument`** (4 types: `FieldFunctionArgument`, `ConstantFunctionArgument`, `NestedFunctionArgument`, `ArithmeticFunctionArgument`): Function arguments now use `SelectCondition` directly, same type as SELECT columns.

### Refactor

- **Backend** (`SqlAgent.Service`):
  - `BasicWhereCondition` gained `Values` property for IN support.
  - `OperationSelectCondition.Left`/`Right` changed from `SelectArithmeticCondition` to `SelectCondition`.
  - `FunctionSelectCondition.Arguments` changed from `List<SqlFunctionArgument>` to `List<SelectCondition>`.
  - Same change applied to `SqlFunctionCondition.Arguments`, `FunctionOrderByCondition.Arguments`, `FunctionGroupByCondition.Arguments`.
  - `ResolveSource()` alias resolution fixed: outer `Alias` now takes priority over `fromQuery.Alias`.
- **Frontend** (`query-definition.ts`, `useSqlBuilder.ts`, `SqlBuilderTabWhere.vue`):
  - Type definitions fully aligned with new backend schema.
  - `"in"` type removed from `WhereItem` union; IN conditions mapped to `type: "basic"` + `operator: "IN"`.
  - UI: IN is no longer a separate dropdown option — shown automatically when operator is `IN`/`NOT IN`.

## [1.6.0-alpha] - 2026-05-24

### Feature

- **Semantic Layer Update MCP Tool**: Added `UpdateSemanticLayer` MCP tool to upsert display names and descriptions for tables/columns, enriching schema discovery results.
- **CI/CD Pipeline**: Added GitHub Actions workflow (`.github/workflows/test.yml`) running `Admin.Test`, `ToolBox.Test`, `SqlAgent.Test`, and frontend tests on release.

### Infrastructure

- New `Infrastructure.csproj` project with corresponding test project.
- Added `ToolBox.Test`, `Common.Test`, `Infrastructure.Test` to solution.

### Tests

- **McpAccessKeyAuthMiddlewareTests** (+221 lines): Comprehensive coverage for MCP auth middleware — valid/invalid keys, missing Authorization, `X-MCP-Server-Key` header fallback, CORS origin enforcement, and context item propagation.
- **CustomToolProxyTests** (+196 lines): Tests for tool not found, parameter replacement, missing SQL config, DML execution, and audit logging on success/failure.
- **SanityTests**: Added to both `Common.Test` and `Infrastructure.Test`.

### Fix

- **Frontend `useVModel` removal**: Replaced `useVModel` (from `@vueuse/core`) with native `computed` getter/setter in `Input.vue`, `Textarea.vue`, and `PasswordInput.vue` to reduce dependency footprint. Added `value` prop as fallback for `modelValue`.
- **PasswordInput.vue**: Renamed internal `value` computed → `model` to avoid prop name collision; added `value` prop support.
- **db-management Select binding**: Fixed `Select` component to use explicit `:model-value` / `@update:model-value` instead of `v-bind="field"` for proper v-model compatibility.

## [1.5.1-alpha] - 2026-05-22

## fix form create/edit not work

## [1.5.0-alpha] - 2026-05-18

### Breaking Change

**Custom tools must be rebuilt** — old custom tools will fail. please rebuild the custom tools.

- **Table access validation**: Queries & DML now verify every referenced table against an API key whitelist (`McpContextItemKeys.TableWhitelist`). Missing permissions throw `UnauthorizedAccessException`.
- **Frontend type system overhaul**: All query definition interfaces migrated to **discriminated polymorphic union types** (e.g., `SelectCondition = FieldSelectCondition | OperationSelectCondition | FunctionSelectCondition | ...`). Old-format saved query definitions are **incompatible**.
- **Recursive table reference collection**: `CollectReferencesAndAliases` now traverses nested `FunctionSelectCondition`, `OperationSelectCondition`, `CaseWhenSelectCondition`, subquery arguments, and window definitions. Old custom tools that relied on simpler traversal may miss table references.
- **SqlKata.Service submodule** updated.

### Feature

- **Table whitelist enforcement** — `CustomToolProxy.ValidateAllTableAccess()` for both `QueryDefinition` and `DmlDefinition`.
- **SQL builder frontend enhancements**:
  - JOIN type selection, alias, and ON conditions reworked.
  - Function-based + field-based ordering with FILTER clauses.
  - Full polymorphic condition editor (basic, column_compare, IN, subquery, nested groups).
  - Comprehensive CRUD for select columns (arithmetic, function, case_when, subquery), order/group/having conditions.

### Refactor

- `HavingOpToSql` → `GetOperatorString` for clarity.
- `CollectFrom*` methods extracted for reuse across proxy validation and execution.
- `CollectFromQueryDefinition` now also collects HAVING, ORDER BY, GROUP BY references.

### Tests

- **+506 lines** of new strategy tests covering Orders table JOINs, WHERE, HAVING, ORDER BY, GROUP BY, window functions, CASE WHEN, arithmetic expressions, CTEs, and UNION.
- All strategy test files (SQLite, Postgres, MySql, SqlServer, Oracle, Firebird) updated to pass the extended test matrix.

## [1.4.2-alpha] - 2026-05-16

### Changed

- **Feature**:
  - Refactor and expand SQL model classes to support advanced function and arithmetic expressions in query building.
  - Introduce `SelectArithmeticCondition`, `SqlFunctionCondition`, and `SqlFunctionArgument` models for flexible, nested SQL function and arithmetic support.
  - Enable recursive arithmetic and function composition in select, group by, order by, and having clauses.
  - Add rich metadata and validation for each model property, improving API documentation and client usability.

#### Details:

- `SelectArithmeticCondition` now supports:
  - Field, constant, left/right operands, operator, and nested function or arithmetic nodes for complex expressions.
  - Example: Support for expressions like `(price * quantity) * (1 - discount)` and nested SQL functions.
- `SqlFunctionCondition`:
  - Represents SQL functions (aggregate/scalar) with a name and ordered argument list.
  - Supports nesting and composition (e.g., `ROUND(AVG(price), 2)`).
- `SqlFunctionArgument`:
  - Allows each function argument to be a field, constant, nested function, or arithmetic expression.
  - Enables deep composition for advanced SQL scenarios.
- All new/updated models are used in select, group by, order by, and having clauses for full query flexibility.

> **Note:** These changes are not breaking but significantly enhance the expressiveness and composability of SQL queries generated by the agent.

## [1.4.1-alpha] - 2026-05-15

### Changed

- **Feature**:
  - Add support for SQL function expressions
  - Enhance arithmetic conditions

## [1.4.0-alpha] - 2026-05-14

### Breaking Change

- **Remove "Configure Manually" mode**: MCP API keys now require association with a Database Management entry (`DbManagementId`). Legacy manual connection string input has been removed from both the API and UI.
- Old MCP keys will no longer be able to connect to the database. Please regenerate new MCP keys.

### Feature

- **Table Whitelist**: Administrators can now restrict each MCP API key to specific database tables. When a whitelist is configured, `get_tables` results are filtered, and `get_columns` / `execute_query_safe` / `execute_dml_safe` will reject access to non-whitelisted tables.
- **Semantic Layer**: Added a semantic metadata layer for databases, allowing administrators to define display names and descriptions for tables and columns. Semantic data is automatically merged into `get_tables` and `get_columns` MCP tool responses.
- **Dynamic Breadcrumb**: The layout breadcrumb now dynamically reflects the current route path with proper labels and navigation links.

## [1.3.17-alpha] - 2026-05-13

### Feature

- UI improve for sql multi select and display.

## [1.3.16-alpha] - 2026-05-11

### Feature

- **SQL Server**: Add support `TrustedServerCertificate` and `Encrypt` options in the connection string when testing or using the database connection.

## [1.3.15-alpha] - 2026-05-02

### Changed

- **CustomToolProxy Enhancement**:
  - Integrated audit logging capabilities to monitor tool execution.
  - Improved query value parsing for more robust handling of dynamic inputs.
- **SQL Builder Refinement**:
  - Added support for **Table Aliases**, enabling more complex and readable JOIN queries.
  - Refactored **Column Options** to provide a more flexible and scalable configuration structure.

### Fixed

- **Sql Definition Json Builder**: Resolved an issue where the JSON builder incorrectly mapped schema definitions.
- **Custom Tool Param Issue**: Fixed a bug where custom tool parameters failed to pass through the proxy, ensuring correct argument delivery to underlying services.

## [1.3.14-alpha] - 2026-04-30

### Features

- **Testing Infrastructure**: Added comprehensive unit and integration test suites for all database strategies (**PostgreSQL**, **MySQL**, **SQLite**, **SQL Server**, **Oracle**) using **Testcontainers**, ensuring reliability across different database engines.
- **Database Management**: Introduced `DbManagementService` and corresponding API controllers to manage database connections and metadata more efficiently.
- **Validation Layers**: Integrated **FluentValidation** on the backend and **VeeValidate** on the frontend to provide robust data validation and improved user feedback.
- **Frontend Enhancements**: Initialized **Vitest** for frontend unit testing and added **MCP configuration** logic to support dynamic tool discovery.
- **Oracle & SQL Server Support**: Fully implemented and validated strategies for Oracle and SQL Server, including container-based integration tests.

### Fix

- **SqlKata Query Builder**: Fixed regressions in the SQL compiler related to subquery alias generation and side effects in column alias handling.
- **Error Mapping**: Improved database error code handling in `BaseStrategy` to provide more accurate troubleshooting hints (e.g., column/table not found).
- **MySQL Compatibility**: Fixed a bug where certain MySQL truncation errors (e.g., Error 1292) were not being correctly intercepted.

### Refactor

- **Code Cleanup**: Streamlined `using` directives and improved general code readability and maintainability across the backend modules.
- **Contribution Guidelines**: Updated `CONTRIBUTING.md` and added issue templates to improve the development workflow.

## [1.3.13-alpha] - 2026-04-24

### Features

- **Visual SQL Builder**: Introduced a comprehensive GUI for building SQL Query JSON definitions, allowing users to visually construct complex tools

### Refactor

- **SQL Builder Simplification**: Removed mandatory table alias (`mainAlias`) logic from the SQL builder to simplify the user interface and generated JSON structure.

## [1.3.12-alpha] - 2026-04-22

### Features

- Added **Database Management** page.
- Enabled direct association with existing databases when issuing **MCP API Keys**.

## [1.3.11-alpha] - 2026-04-21

### Breaking Change

- replace AES encryption with AesGcm for improved security and performance in connection string encryption and decryption : this is a breaking change that requires existing encrypted connection strings in the database to be re-encrypted using the new AesGcm-based CryptoService implementation, as the encryption format and key management are updated for enhanced security.

## [1.3.10-alpha] - 2026-04-20

### Feature

- **Custom SQL Tools (Low-Code Tool Plugin System)**: Administrators can now define domain-specific SQL operations (Query or DML) directly from the Admin Panel, exposing them as new MCP tools to the AI agent.
- **Dynamic Parameter Injection**: Introduced `{{parameterName}}` syntax for custom tools, allowing the AI to pass context-aware arguments into pre-defined SQL statements.
- **Admin UI Enhancements**: Added a dedicated management interface for creating, testing, and managing Custom SQL Tools.
- **Documentation Refactoring**: Major overhaul of `README.md` with a high-speed aesthetic, improed information architecture, and integrated Admin Panel snapshots.

## [1.3.9-alpha] - 2026-04-19

### Security Issue Fix

- Resource injection For ConnectString
- Clear text storage of sensitive information
- Exposure of private information
- Log entries created from user input

### Breaking Change

- UI Change: Manual entry of ADO.NET connection strings is disabled in the UI.

## [1.3.8-alpha] - 2026-04-18

### Improvement

- Remove unnecessary middleware to reduce performance consumption

### Fix

- Fix the issue where the dashboard is not displaying

## [1.3.7-alpha] - 2026-04-16

### Feature

- `allowed Tools` For Dynamic Tool List
- implement database connection testing feature 、 global provider support

## [1.3.6-alpha] - 2026-04-16

### Feature

- `allowed Tools` Manage tool access for the API key : This feature allows administrators to specify which tools or API endpoints an issued API key has access to. By managing tool access at the API key level, you can enforce fine-grained permissions and restrict certain keys to only use specific functionalities of the MCP API, enhancing security and control over how the API is used.

## [1.3.5-alpha] - 2026-04-16

### Fix

- Fix Where Condition IN/NOTIN

## [1.3.4-alpha] - 2026-04-12

### Breaking Change

- remove `get_table_reference` tool from MCP API: this tool was rarely used and added complexity to the codebase, and its functionality can be achieved through a combination of `get_tables` and `get_columns` calls. Removing it simplifies the API and reduces maintenance overhead without significantly impacting usability.
- Added support some new database providers: `SqlServer`, `Oracle`, and `FireBird`. The MCP API can now handle connections to these additional database types, expanding the range of supported databases for SQL query execution and metadata retrieval.
- Added `execute_dml_safe` tool to MCP API: this new tool allows clients to execute DML statements (INSERT, UPDATE, DELETE) safely through the MCP API, with the same security guards and validation mechanisms as `execute_query_safe`. This expands the capabilities of the MCP API to support data modification operations in addition to query execution.

## [1.3.3-alpha] - 2026-04-11

### Refactor

- Refactor SQL strategy classes and improve query handling

## [1.3.2-alpha] - 2026-04-09

### Bug Fix

- Fixed MySQL case-insensitive string filters by avoiding unsupported `ILIKE` and using a compatible `LOWER(field) LIKE lower(pattern)` path.
- Fixed `combineConditions` handling for `union all` by normalizing combine type values (`union all`, `union_all`, `unionall`).
- Improved query error output strategy by database type, including PostgreSQL-specific guidance for `42P01` (relation/CTE reference not found).
- Updated `get_tables` flow to require an explicit schema name so table discovery is deterministic across database providers.

## [1.3.1-alpha] - 2026-04-08

### Feature

- form validation login and register

## [1.3.0-alpha] - 2026-04-08

### Breaking Change

- implement ICryptoService for db connection string encryption and decryption : this is a breaking change that requires existing encrypted connection strings in the database to be re-encrypted using the new CryptoService implementation.

## [1.2.0-alpha] - 2026-04-07

### Refactor

- module structure move

### Security

- enhance SQL query execution with security guards and aggregation validation

## [1.1.1-alpha] - 2026-04-07

### Bug Fix

- Fixed a key-validation mapping issue where `SqlConnectionString` was not returned in `McpAccessKeyValidationResult`, which could cause MCP runtime database connection resolution to fail for valid keys.

## [1.1.0-alpha] - 2026-04-06

### Performance & Security

- **Robust MCP Key Authentication Cache:** Implemented `IMemoryCache` with a SHA256 hashed cache-key and Striped Locking mechanism (`SemaphoreSlim`) to safely prevent cache stampede issues in high-concurrency environments while maintaining minimal memory overhead.
- **Global IP-based Rate Limiting:** Simplified the rate limiting flow by removing per-key dynamic rate limit overrides. The MCP API now strictly relies on a global, IP-bucketed rate limiter configurable via `appsettings.json`, ensuring stable traffic control before authentication checks.
- **Pre-serialized Error Responses:** Optimized overhead by returning pre-serialized byte arrays for frequent error responses (`401 Unauthorized`, `403 Forbidden`) during key evaluation.

### Refactor

- Stripped rate limit properties (`PermitLimitOverride`, `WindowSecondsOverride`, `QueueLimitOverride`) out of the database entity `McpAccessKey`, ViewModels, and the `McpAccessKeyService`.
- Reordered ASP.NET Core middleware in the `/mcp` pipeline to execute rate limiting prior to identity validation.
- Cleaned up the frontend UI (`index.vue`) by safely removing unused rate limiting fields and logic during API key issuance.

## [1.0.0-alpha] - 2026-04-05

### Feature

- Initial release of hs-sql-agent backend and frontend modules.
