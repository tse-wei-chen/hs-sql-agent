# PostgreSQL 16 — physical lost COMMIT / ROLLBACK acknowledgements (#575)

This evidence belongs to PR #588. It is a first-provider expansion of the
SQLite transaction-uncertainty safety proof, **not** an assertion that the other
five database providers share PostgreSQL transaction/transport semantics.

## Independent real database control

The PostgreSQL 16 service in the existing \`sqlagent-test\` GitHub Actions job
is used through the first-party \`PostgresProvider\` and native
\`NpgsqlConnection\`/\`NpgsqlTransaction\`. Each test creates a distinct
throwaway schema with real \`items(id PRIMARY KEY, note)\` data. DML is parsed
through the F# SqlCore compiler and approved by \`TypedDmlRuntime\`.

A \`DbConnection\` decorator forwards actual SQL commands, connections and
transactions to Npgsql. The only fault is injected *after* a successful native
\`CommitAsync\` or \`RollbackAsync\`, at the acknowledgement boundary:

- **Lost COMMIT ACK:** two independently previewed INSERTs commit atomically.
  The typed runtime emits \`DmlCommitOutcomeUnknownException\`, not a confirmed
  failure. A fresh PostgreSQL witness connection independently observes
  both rows.
- **Lost ROLLBACK ACK:** after preview, a separate PostgreSQL connection commits
  a conflicting primary key. The first approved INSERT runs, the second raises
  a genuine database constraint violation, and native PostgreSQL ROLLBACK
  completes before its acknowledgement is dropped. The typed runtime emits
  \`DmlRollbackOutcomeUnknownException\`. The independent witness sees only
  the racing row and no first INSERT.
- Both paths consume the one-time approval challenge. Directly reusing the
  identical approved session is rejected without additional physical writes.

## Published Custom MCP and persisted Audit

\`PostgresPhysicalUnknownOutcomeHostTests.cs\` repeats both cases through the
published \`archive_order\` Custom MCP Tool and external approval callback. It
asserts \`status=outcome_unknown\`, \`retryable=false\`, the correct distinct
\`dml.*_outcome_unknown\` code and exact originating approval request ID.
SQL literal values must not be reflected.

An independently opened, **file-backed SQLite Admin Audit Store** (separate
from the real PostgreSQL target) must show \`outcome_unknown\`, the expected
terminal error category, approved status, no asserted affected-row count, the
incoming HTTP/MCP trace ID and an indexed/searchable approval request ID.
PostgreSQL target counts and inserted row identity are separately re-read
from another actual Npgsql connection after every attempted operation.

The generic \`server-test\` suite excludes this mandatory-live class; a
dedicated step in \`sqlagent-test\` selects it against PostgreSQL 16, with a
missing required connection treated as failure in CI, not a skip.

## Boundary and release implications

The test proves **physical PostgreSQL transaction outcome, MCP response,
persisted Admin Audit and no-replay protection** for one approved ordered
two-statement INSERT batch and simulated post-native-transaction lost
acknowledgements. It does not validate real TCP disconnections, server crashes,
cross-instance Redis approval consumption, distributed SQL concurrency
limiting, third-party providers, the other four non-SQLite databases, or
real-world production connection-pool sizing. SQLite Audit persistence is an
integration harness and not a PostgreSQL-backed Admin Store assertion.

\`#575\` stays Open. Do not publish \`3.1.0\` before all remaining release gates
have independent evidence and exact-head CI success.
