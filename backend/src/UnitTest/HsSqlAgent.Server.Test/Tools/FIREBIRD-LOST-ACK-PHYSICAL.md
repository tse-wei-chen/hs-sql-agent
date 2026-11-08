# Firebird 5 — physical Custom DML lost COMMIT/ROLLBACK acknowledgements (#575)

This is a bounded test of **real Firebird 5 DML transaction outcomes** with
synthetic *post-native* acknowledgement loss. It is not a claim that every
transport loss, crash, provider version or DDL operation behaves the same.

## Native provider and independent witness

The existing Firebird 5 GitHub Actions service job runs
`FirebirdPhysicalUnknownOutcomeIntegrationTests` against the real native
`FbConnection` / `FbTransaction` implementation. The generic unit-test
job excludes this class; missing `TEST_FIREBIRD_CONNECTION` fails the
mandatory service step instead of being treated as a passing skipped test.

Every scenario creates a unique physical table with an integer primary key,
and obtains its database identity from a real
`SELECT TRIM(MON$GUID) FROM MON$DATABASE` query. The native Firebird
metadata/capability path and existing **read-only preview transaction TPB**
remain in use during approval preview. The acknowledgement-dropping
`DbConnection` / `DbTransaction` decorator is only enabled for the
approved transaction. It forwards native SQL and COMMIT or ROLLBACK, and
injects `IOException` **after** the native acknowledgement would have
been returned.

- **COMMIT ACK lost:** direct `TypedDmlRuntime` must classify a
  `DmlCommitOutcomeUnknownException` while a different real Firebird
  connection sees both approved inserts.
- **ROLLBACK ACK lost:** after approval preview a distinct real Firebird
  connection commits a conflicting primary key. The second approved insert
  fails after the first statement has run; native ROLLBACK completes before
  acknowledgement loss. A fresh connection sees only the racing row.
  Direct runtime must classify `DmlRollbackOutcomeUnknownException`.
- **No replay:** the same direct approval session must be consumed after
  either unknown outcome, and subsequent reuse cannot write.
- **Custom MCP + persisted Audit:** both paths run via the published
  Custom MCP proxy, with `status=outcome_unknown`,
  `retryable=false`, distinct commit/rollback codes, the exact originating
  approval request ID, and no SQL secret literals. An independently
  re-opened **SQLite Admin Audit Store** must contain a searchable approval
  ID, incoming request ID, terminal error category and approved status.
  Actual Firebird rows are verified through another `FbConnection`.

## Boundaries

This is synthetic post-native loss, not actual TCP disruption, DB process
crash, failover, Redis distributed-consent safety, Firebird-backed Admin
Audit, arbitrary UDF side effects, or comprehensive pool/admission load
proof. It does not change production SqlCore, MCP or provider code.

The Firebird slice counts as accepted only after the live step has 0 skipped
cases and the exact PR head has 18/18 Tests jobs and SqlCore baseline
comparison GREEN. #575 still requires Query function, multi-provider
concurrency/load and release evidence gates before v3.1.0 can be published.
