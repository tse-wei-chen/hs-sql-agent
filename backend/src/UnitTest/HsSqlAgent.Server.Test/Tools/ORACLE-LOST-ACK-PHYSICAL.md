# Oracle 26ai — physical Custom DML transaction acknowledgement-loss evidence (#575)

This test covers **Oracle DML transactions only**; Oracle schema DDL commonly
performs implicit commits and must not inherit this conclusion.

## Real target and bounded fault

The existing GitHub Actions `sqlagent-oracle-ddl-parity` job launches the
version-pinned Oracle 26ai service and supplies
`TEST_ORACLE_CONNECTION`. This class is excluded from the generic
`server-test` shard and selected in a mandatory live-provider step: a
missing connection in CI is an error, not a skip.

The approval identity comes from the live session\'s
`SYS_CONTEXT('USERENV','CON_NAME')`, rather than `OracleConnection.Database`
(which may be blank); this preserves the required nonempty physical PDB binding.

A fresh `SQLCORE` table with a real `NUMBER(10,0)` primary key and
`VARCHAR2` note is used in each scenario. The F# SqlCore typed DML
compiler parses an approved two-statement INSERT batch. A test-only
`DbConnection` / `DbTransaction` decorator forwards all SQL and actual
`OracleTransaction.CommitAsync` / `RollbackAsync` calls to ODP.NET.
Only **after** the native transaction call returns successfully does it
throw a synthetic `IOException` to emulate loss of acknowledgement.

- **COMMIT ACK loss**: the direct typed runtime raises
  `DmlCommitOutcomeUnknownException`; an independent fresh ODP.NET
  connection must observe both rows.
- **ROLLBACK ACK loss**: a separate real Oracle connection commits a
  conflicting primary key after approval preview. The first statement
  executes in the target transaction; the second fails. Native ROLLBACK
  executes before the acknowledgement is discarded. A witness sees only
  the competing row, with no partial first INSERT.
- **No replay**: the consumed aggregate approval session is rejected on
  a second direct commit attempt after either terminal exception, without
  further physical mutation.
- **Custom MCP and durable Audit**: `CustomToolProxy.Execute` repeats
  both paths through the actual provider and approval callback, returning
  distinct `dml.commit_outcome_unknown` /
  `dml.rollback_outcome_unknown`, `retryable=false`, exact
  `approvalRequestId`, and no SQL literal values. An independently
  reopened, file-backed **SQLite Admin Audit Store** must contain the
  correct terminal category, approved status, original incoming request
  trace and searchable approval request correlation. The physical Oracle
  target is checked independently.

## Limits

These tests inject lost acknowledgement after real ODP.NET success.
They do **not** reproduce actual TCP loss, server failover, crash recovery,
Redis cross-process consent, independent Oracle Audit Store backing, or
nontransactional Oracle DDL. No claim is made about unrestricted native UDF
safety, arbitrary SQL or other database providers.

A passing exact PR head (Oracle live step, all 18 Tests Jobs and SqlCore
baseline comparison) closes only the Oracle-specific bounded evidence gap;
#575 stays open until Firebird and the other Query, concurrency and release
gates are satisfied.
