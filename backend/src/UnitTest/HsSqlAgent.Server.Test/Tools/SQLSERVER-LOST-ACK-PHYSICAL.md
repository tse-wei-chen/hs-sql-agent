# SQL Server 2022 — real physical DML transaction acknowledgement-loss proof (#575)

This is a **target-specific release-gate test**, not a general claim about SQL
Server reliability or all six providers. It follows the existing PostgreSQL
and MySQL Custom DML proof but uses a real SQL Server 2022 instance and
`Microsoft.Data.SqlClient` rather than an in-memory transaction stub.

## What the mandatory CI step exercises

The `sqlagent-fence-sqlserver` GitHub Actions job launches SQL Server 2022
and runs `SqlServerPhysicalUnknownOutcomeIntegrationTests` separately from
the general unit-test shard. Missing `TEST_SQLSERVER_CONNECTION` in CI is a
failure, not a skip.

Each of the two scenarios uses a uniquely named physical `dbo` table with
a primary key, two SQL statements handled by the F# SqlCore DML pipeline,
a bounded policy, and an approval challenge:

- **Lost COMMIT acknowledgement:** the native `SqlTransaction.CommitAsync`
  completes, then a test-only `DbTransaction` decorator throws `IOException`.
  Independent `SqlConnection` witnesses must see both committed inserts.
  Direct `TypedDmlRuntime` must report `DmlCommitOutcomeUnknownException`.
- **Lost ROLLBACK acknowledgement:** a separate SQL Server connection commits
  a competing primary key after approval preview. The first DML statement
  executes; the second conflicts. Native `SqlTransaction.RollbackAsync`
  completes, then the decorator throws `IOException`. A fresh connection
  must see only the independently committed conflicting row, not the first
  attempted insert. Direct runtime must report
  `DmlRollbackOutcomeUnknownException`.
- **Consumed approval:** both terminal paths reject a second direct commit
  attempt with the same one-use approval session, with no extra physical write.
- **Published Custom MCP + durable Audit:** the same two scenarios run through
  `CustomToolProxy.Execute`, the real provider and external approval callback.
  They must return `status=outcome_unknown`, `retryable=false`, the distinct
  `dml.commit_outcome_unknown` or `dml.rollback_outcome_unknown` code and the
  exact originating approval request ID without SQL values. An independently
  reopened file-backed **SQLite Admin Audit Store** must contain searchable
  approval correlation, the matching unknown-outcome category, approved status
  and the incoming request trace. SQL Server target state is separately
  re-read through `SqlConnection`.

## Limits and release implication

The injected fault occurs **after a real native COMMIT/ROLLBACK succeeds**;
it is an acknowledgement-loss test, **not** a real TCP disconnect, server
crash, failover, distributed Redis approval or network partition. The Audit
Store is SQLite, not a SQL Server-backed Admin Store. This test does not
prove every SQL Server transaction mode or pool configuration.

The SQL Server test closes only its own bounded evidence gap when the exact
PR head's mandatory live tests, all Tests jobs and SqlCore baseline comparison
succeed. Oracle and Firebird lost-ACK proof and the other #575 security
release gates remain open; do **not** publish 3.1.0 on the strength of this
test alone.
