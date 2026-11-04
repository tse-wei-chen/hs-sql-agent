# Changelog

All notable changes to this project will be documented in this file.

## [3.0.0] - Unreleased

### G9/M13 — PostgreSQL native typed conflict UPDATE values (develop only)

- Expand the proven single-row `ON CONFLICT (id) DO UPDATE SET col = EXCLUDED.col` Host approval from 1–4 `TEXT NOT NULL` columns to **1–4 mixed `TEXT NOT NULL` and exact `BIGINT NOT NULL` value columns**. The sole physical key remains NOT NULL BIGINT and must have the table's only simple, immediately enforced UNIQUE/PRIMARY index. An exact integer literal is required for BIGINT; string casts, fractional/exponent numbers, NULL, overflow and mismatched physical types fail closed.
- Reuse the existing F# closed DML AST, compiled mutation, per-field before/after preview, one-time approval, whole-row fingerprint and PostgreSQL transaction-held `SHARE ROW EXCLUSIVE NOWAIT` lock. The Host's full physical Catalog proof now compares each declared value column's actual TEXT/BIGINT type, NOT NULL and absence of defaults, extra columns/indexes, CHECK/foreign keys, user triggers, rewrite rules, partitioning, inheritance and RLS. Every old value and proposal is still bound to the same approval; no new MCP tool or public DML API.
- Version native typed approval bindings to v3 and seal the BIGINT column list: **in-flight M12 previews require a fresh Preview** after deployment. Existing TEXT-only UPSERT and SQLite/PostgreSQL DO NOTHING remain valid. Table-lock write contention is intentional, not a measured speedup; dates, floating point, smaller integers, NUMERIC and nullable values remain unsupported.


### G9/M12 — Bounded PostgreSQL multi-column conflict UPDATE approval (develop only)

- Reuse M11's native PostgreSQL typed Host `ON CONFLICT DO UPDATE` plan, Preview, one-time approval and commit-time `SHARE ROW EXCLUSIVE NOWAIT` physical table lock; support **1–4 exactly matching `TEXT NOT NULL` proposed columns**, including new 2–4 value-column cases. The source is one VALUES row with an exact non-NULL BIGINT sole simple immediate PRIMARY/UNIQUE key; assignments must cover every inserted non-key column through `SET column = EXCLUDED.column`, without duplicates, other expressions, NULL or partial updates.
- A permanent ordinary target must consist of exactly the key plus those value columns; no extra columns, indexes, defaults, CHECK/FK constraints, user triggers, rules, inheritance, partitions or RLS. Preview exposes an `INSERT`/`UPDATE` action with **before and after values for every column**; their complete old-row fingerprint, exact proposal, physical catalog SHA and compiled SQL are sealed in the challenge. Commit revalidates all values and cancels the whole operation on any drift, without new MCP tools, APIs, drivers or changes to the default four read-only tools / DML unchecked.
- Updated-column proof and challenge-binding version are intentionally bumped. In-flight M11 previews must be requested again when rolling to M12; pre-existing ordinary DML and `DO NOTHING` flows are preserved. Table-lock writer contention remains the explicit cost. No compiler performance improvement is claimed.


### G9/M11 — PostgreSQL one-row conflict UPDATE with immutable Before/After Host approval (develop only)

- Extend typed Host DML from `ON CONFLICT DO NOTHING` to the narrowly proven PostgreSQL `ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name` one-row path. The physical table must contain exactly a NOT NULL BIGINT sole simple immediate PRIMARY/UNIQUE key and one TEXT NOT NULL value column, without any defaults, CHECK/foreign keys, extra indices, triggers, rewrite rules, partition/inheritance or RLS.
- Preview binds either INSERT (before value absent) or UPDATE (old actual value shown) plus proposed new value and full old-row fingerprint to one-time approval. Commit takes a `SHARE ROW EXCLUSIVE NOWAIT` transaction-held table lock, rechecks the complete catalog digest and old row, and executes the F# compiled command only once. New/conflicting rows, old-value changes, index drift and changed policy cause refusal.
- Generic UPSERT, arbitrary SET expressions, updating the key, UPDATE WHERE, multiple assignments/rows, RETURNING and DML batch remain denied. Existing SQLite/PostgreSQL DO NOTHING and four default read-only tools with DML unchecked are unchanged. Strong lock contention is an intentional safety-first tradeoff; no throughput improvement is claimed.


### G9/M10 — PostgreSQL native single-row DO NOTHING approval (develop only)

- Extend the existing SQLite bounded `ON CONFLICT (id) DO NOTHING` Host approval to native PostgreSQL, preserving the F# strict single-row/column shape and one-time row-set approval.
- The built-in PostgreSQL 16 provider must prove the **sole simple immediately enforced (NOT DEFERRABLE) PRIMARY or UNIQUE key**, exact non-NULL INT2/INT4/INT8 physical type, real permanent plain relation, no trigger/rule/partition/RLS, and full index/constraint/catalog fingerprint. Preview binds 1 insert or 0 conflict skip; commit takes a PostgreSQL `SHARE ROW EXCLUSIVE NOWAIT` table lock before comparing full schema and actual conflict state, then executes only the F# compiled parameterized INSERT once. Index drift, a changed conflict outcome or changed approval fails closed.
- The strong table lock deliberately blocks concurrent writers during commit-time proof; this is a narrow safe first slice, not a low-contention general upsert. Partial/expression/composite/multiple unique keys, NULL/text/UUID conflict keys, DO UPDATE, RETURNING, multi-row and atomic batches remain denied. No new MCP tool, public API, dependency or frontend default change.


### G9/M10 — SQLite sole physical UNIQUE conflict approval (develop only)

- Extend the existing single-row SQLite `ON CONFLICT (id) DO NOTHING` Host Approval from a sole PRIMARY KEY to a **sole simple physically enforced UNIQUE column or ordinary nonpartial UNIQUE index**. Reuse the same F# structural validator, native metadata, one-row payload, preview conflict-state fingerprint, one-time challenge, SQLite IMMEDIATE transaction and live freshness recheck. The key remains an explicit non-NULL exact INT64 literal; no generic UPSERT is enabled.
- Reject multiple/partial/expression/composite unique keys, non-unique indexes, triggers, DO UPDATE, RETURNING, multirow and batch forms. An index revoked between Preview and Commit invalidates the approval before any write. Existing PK behavior is unchanged.
- Real SQLite positive and negative live tests cover insert/skip and schema-index revocation. The internal versioned approval binding distinguishes the new sole-UNIQUE-key proof; stale approvals require a fresh Preview. No new NuGet/public API, MCP tool or driver and no frontend default change.

### G9/M10 — native SQLite PRIMARY KEY `ON CONFLICT DO NOTHING` Host approval (develop only)

- Add the first **narrow native conflict Host approval** without promoting general Upsert/MERGE: one SQLite `INSERT VALUES` row with an explicit, non-NULL exact INT64 PRIMARY KEY literal; the PK must be the **sole physical enforced unique key**, policy `DmlMaxAffectedRows=1`, built-in native SQLite provider, native target schema capture and commit freshness. Any trigger-bearing target is rejected because trigger side effects are outside the approved one-row count. `DO UPDATE`, `RETURNING`, targetless conflict, multiple rows, alternate/composite/extra unique keys, non-SQLite dialects and atomic batches remain fail-closed.
- Host Preview reads the actual PK match, binds an expected **1 INSERT vs 0 DO NOTHING** result and immutable payload in the one-time Approval challenge. Commit rechecks the exact PK set in SQLite's IMMEDIATE write transaction, pins schema freshness, checks actual affected row count, and rolls back any drift. Existing approval context, policy, nonce, transaction witness and target fence contracts are reused; no new public API, MCP tool, driver, or generic conflict execution path.
- F# Strict Schema Snapshot now proves only the one-row explicit-target SQLite `DO NOTHING` column shape; `DO UPDATE` and other unproven UPSERT assignments remain rejected in strict mode. Physical uniqueness stays a separate Host-owned live proof.
- Real SQLite file-database Host integration and SqlCore positive/negative regression tests cover insert, skip, replay, conflict presence/absence drift, schema/index revocation, ambiguous uniqueness, trigger effects, unproven expressions and batch rejection. Existing four selected read-only frontend tools and DML disabled default are unchanged.

### G9/M10 — SQL Server native single-row MERGE target key-range locking (develop only)

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

### G9/M09 — bounded direct-column NULL predicates for native INSERT SELECT (develop only)

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

### G9/M09 — SQLite bounded INSERT SELECT in ordered atomic DML approval (develop only)

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

### G9/M09 — bounded native BETWEEN for four physical INSERT SELECT sources (develop only)

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

### G9/M09 — four-provider bounded native INSERT SELECT IN/OR source predicates (develop only)

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

### G9/M09 — SQL Server 2022 native INSERT SELECT OUTPUT INSERTED (develop only)

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

### G9/M09 — SQL Server 2022 single-physical-table INSERT SELECT (develop only)

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

### G9/M09 — SQL Server 2022 physical INSERT SELECT source-row approval (develop only)

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

### G9 / M09 — Bounded conjunctive native INSERT SELECT source predicates (develop only)

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

### G9 / M09 — MySQL 8.4 InnoDB same-table INSERT SELECT approval (develop only)

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

### G9 / M09 — MySQL 8.4 InnoDB INSERT SELECT source-row approval (develop only)

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

### G9 / M09 — PostgreSQL self-source INSERT SELECT approval (develop only)

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

### G9 / M09 — PostgreSQL two-table INSERT SELECT approval (develop only)

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

### G9 / M09 — SQLite INSERT SELECT direct RETURNING result rows (develop only)

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

### G9 / M09 — SQLite dual-physical-table INSERT SELECT approval (develop only)

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

### G9 / M09 — SQLite self-source INSERT SELECT approval (develop only)

- Admit only explicitly bounded native SQLite `INSERT INTO main.table (columns) SELECT direct_columns FROM main.table WHERE column = literal`, with the source and target being the **same physical main table**. Preview hashes every projected source value, including duplicate rows; the approved row count and immutable compiler-generated SQL are bound into a one-time challenge.
- A real SQLite IMMEDIATE write transaction revalidates the physical schema and complete projected source row multiset before executing the approved native insert once. Changed source values, row count, schema, runtime database version or authorization context fail closed or roll back; no new MCP tool.
- Requires explicit `AddHsSqlAgentNativeDmlSchemaFreshness()`, a finite positive DML row limit and existing DML approval. No CTE, JOIN, aggregate, expression projection, RETURNING, UPSERT, independently unattested second source, attached database or atomic DML batch. Other five providers remain unchanged.

This is a **major-version release candidate**, not an already published tag. All first-party NuGet
packages use `VersionPrefix=3.0.0` in this branch. See the [documentation website](https://sql-agent.net/en/docs/).

### G9 / M08 — bounded SQL Server native multi-source DML (develop only)

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

### G9 / M07 — correlated semi/anti filters inside physical CTE diamond (develop only)

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

### G9 / M06 — bounded CTE diamond direct payload lineage (develop only)

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

### G9 / M06 — bounded key-preserving CTE diamond (develop only)

- Two independent SQL Server CTEs can project the same live UNIQUE source,
  reconverging through a third CTE's complete two-column INNER key JOIN.
  The direct projection still requires both target key equalities.
- F# static cardinality, F# sealed execution admission and Host live index
  discovery remain independent. Physical metadata, approval row-set and
  same-transaction `IsCurrent` remain mandatory.
- All other DAG shapes, extra physical sources, computed/cast keys,
  missing/duplicated JOIN edges, OR, LEFT JOIN, WHERE, aggregation and
  ambiguous aliases are fail-closed. No new tool, API or provider grants.

### G9 / M06 — two-level Derived Table physical key lineage (develop only)

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

### G9 / M06 — bounded Derived Table key-preserving physical lineage (develop only)

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

### G9 / M05 — exact SQL Server linguistic-collation equality (develop only)

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

### G9 / M05 — Nullable SQL Server source UNIQUE + ordinary equality witness (develop only)

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

### G9 / M04 — exact BIN2 VARCHAR/NVARCHAR physical key proof (develop only)

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

### G9 / M04 — SQL Server exact UNIQUEIDENTIFIER physical keys (develop only)

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

### G9 / M04 — exact SQL Server fixed-decimal physical keys (develop only)

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

### G9 / M04 — exact 8/16-bit physical key kinds (develop only)

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

### M07 — correlated semi-/anti-join filters on one attested source (develop only)

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

### M07 — independently attested second physical JOIN source (develop only)

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

### G9 / M04 — SQL Server exact integer key equivalence (develop only)

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

### Programmatic administration

- Add dedicated **Management API Keys** for machine-to-machine access to the existing `/api` management endpoints. Authenticate using `X-API-Key: hsa_mgmt_...`; human sessions continue to use `Authorization: Bearer <JWT>`.
- Provide an Admin UI for issuing, listing, immediately revoking, and rotating Management API Keys, with one-time plaintext display and an explicit allowlist of canonical API permissions.
- Keep Management API Keys **fully separate** from MCP access keys, MCP tool authorization, and SQL execution. Machine keys cannot operate built-in identity/role administration, edit security policy, or issue other management keys.
- Persist hashed credentials with expiry / usage metadata in the Admin Store; add **SQLite and PostgreSQL schema migrations**, plus permission and credential-isolation tests.
- Remove the repository docs directory; the documentation website already provides project documentation.

### Upgrade notes

- Upgrading to 3.0.0 requires the Admin Store migrations for `ManagementApiKeys` before creating keys.
- Existing JWT admin authentication and MCP keys are unchanged. No new API key is issued automatically.
- Management API Keys are currently scoped by canonical API permissions, **not** by database or SaaS tenant; protect these keys as server-to-server credentials.
- Follow the versioned documentation site's Management API Key guide for the request header and examples.

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
