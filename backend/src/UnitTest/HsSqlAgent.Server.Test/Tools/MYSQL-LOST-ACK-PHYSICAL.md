# MySQL 8.4 — physical DML lost COMMIT / ROLLBACK acknowledgements (#575)

PR #589 extends the earlier SQLite and PostgreSQL live DML outcome proofs to **real MySQL 8.4 with InnoDB**. It does not infer physical behavior of other providers from the common C# transaction coordinator.

## Scope and real transaction fault

Every test creates a distinct throwaway MySQL database, and an InnoDB `items(id INTEGER PRIMARY KEY, note VARCHAR(64))` table. The first-party `MySqlProvider`, F# SqlCore DML batch parsing, typed transaction approval and `MySql.Data.MySqlClient` native commands/transactions are exercised. Only the transport *acknowledgement after a native transaction completes* is simulated by a transparent `DbConnection` / `DbTransaction` decorator. The database, transaction and SQL executor are **not mocks**.

- **Lost COMMIT ACK:** two approved `INSERT` statements commit inside an actual `MySqlTransaction`, and only then does the decorator throw `IOException`. `TypedDmlRuntime` reports `DmlCommitOutcomeUnknownException`. A separate MySQL connection verifies two rows and the exact two notes.
- **Lost ROLLBACK ACK:** after approval preview, a separate MySQL connection commits a conflicting ID for statement two. In the approved transaction, statement one executes but statement two causes a real duplicate-key error; a native InnoDB rollback completes, then its ACK is discarded. `TypedDmlRuntime` reports `DmlRollbackOutcomeUnknownException`. An independent MySQL connection verifies *only* the competing row, and no first statement's write.
- **No replay:** immediately retrying either consumed aggregate approval session is denied by the real typed runtime; independent physical row counts remain unchanged.

## Published Custom MCP and persistent Admin Audit

The parallel `MySqlPhysicalUnknownOutcomeHostTests.cs` cases execute the same real two-statement MySQL DML via the published `archive_order` Custom MCP Tool and approval callback. The MCP response must contain `status=outcome_unknown`, `retryable=false`, the corresponding `dml.commit_outcome_unknown` or `dml.rollback_outcome_unknown` code, and the original approval request ID; SQL literals must not be reflected.

The tests separately open a persisted **SQLite-based Admin Audit Store** after the MCP call, assert status `outcome_unknown`, correct error category, `approved` status, missing affected-row claim, HTTP/MCP request trace, exact `ApprovalRequestId` and successful keyword query on the same ID. The Audit Store is intentionally independent of the **MySQL target database** and must not be described as proof of a MySQL-backed Admin Store. Physical MySQL row state is observed again from a different driver connection.

## CI and strict boundaries

The existing `sqlagent-test` job uses the `mysql:8.4` service and explicitly selects `MySqlPhysicalUnknownOutcomeIntegrationTests` with `TEST_MYSQL_CONNECTION`. Missing connection information is a hard CI failure rather than a skip; the generic `server-test` job excludes the mandatory-live class. The proof contains two direct runtime and two published Custom MCP cases.

This tests *post-successful-native-transaction* lost acknowledgements, not real TCP failure, ambiguous server-side transaction completion during network loss, process crash, failover, distributed approval replay across instances, connection-pool exhaustion, or MySQL nontransactional engines and implicit-commit DDL. The four non-MySQL/non-SQLite providers are not covered by these tests, except PostgreSQL's separately verified #588 proof. **#575 and the 3.1.0 publication block remain in effect.**
