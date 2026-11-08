# MySQL 8.4 — physical Query-only read-only transaction guard (#575)

## Threat boundary

An MCP API key granted only `execute_query_sql`, with an empty
`TableWhitelist`, does not imply the target database user lacks DML
permissions. A MySQL stored function declared `MODIFIES SQL DATA` may be
called from a single SELECT and modify a different real table. The F#
SqlCore compiler guarantees statement and modeled SQL validation, not the
physical effects of opaque server functions. A Table Whitelist cannot
see an opaque function body; an absent whitelist cannot be treated as a
database privilege boundary.

## Runtime fix

For **every MySQL Query** the Host now:

1. Opens and verifies the physical target connection and compiles the SQL
   as before.
2. Issues `SET TRANSACTION READ ONLY` on that **same connection**, before
   calling native `BeginTransactionAsync`. This is MySQL's **next
   transaction** scope; it does not change the connection's session
   default or leak a permanent read-only setting into pooled connections.
3. Executes the immutable compiled SELECT with the exact returned native
   transaction bound to the query command, then rolls back and disposes
   the transaction.
4. Fails closed on fence/transaction errors. The old unfenced
   `ExecuteQueryAsync` path is **not** a fallback for MySQL.

Both `TypedQueryRuntime.ExecuteAsync` and `ExecuteWithFactsAsync`
share `ExecuteVerifiedQueryAsync`, so the Query-only MCP path and
direct runtime use the same physical fence. Normal DML and approved
Custom DML transaction paths are unchanged.

## Mandatory real MySQL 8.4 test

`QueryNativeFunctionLiveTests.MySql_QueryOnlyKeyWithoutWhitelist_CannotPersistFunctionSideEffects`
runs in the existing live MySQL 8.4 service step
(`QueryNativeFunctionLiveTests.MySql_`). It creates a disposable
MySQL database with InnoDB `visible_users` and `side_effects` tables,
and a server-defined function returning its argument but declared
`MODIFIES SQL DATA` that inserts into `side_effects` as a side effect.

- A direct real MySQL SELECT must invoke the function and physically
  persist a row: positive attack control. The row is then deleted.
- The same SELECT must compile through F# with `allowedTables=null`;
  this prevents a compiler rejection from masquerading as a physical
  effect fence.
- A published MCP Query tool with only
  `AllowedTools=execute_query_sql` and empty `TableWhitelist` must
  return failure and leave `side_effects` empty.
- Direct `TypedQueryRuntime.ExecuteAsync` must also reject the native
  side effect without a persisted row.
- A normal MySQL `SELECT ABS(id)` must still succeed through the same
  MCP key, with no writes.

The test is mandatory with a real MySQL 8.4 database; missing
`TEST_MYSQL_CONNECTION` fails CI rather than silently skips.

## Limitations

MySQL 8.4 documents that READ ONLY transactions prohibit changes to
tables visible to other transactions, **but still allow changes to
TEMPORARY tables**. This does not prove UDF external I/O or network/process
side-effect safety, that a SELECT cannot acquire any application state,
or that query results obey a data-visibility whitelist if none exists.
Native UDF policies, per-database least-privilege roles and host
deployment constraints are still required. Nor does it close the
SQL Server, Oracle or Firebird effect boundaries or the #575 release
gate. Real provider pool/failure behavior and CI release audit remain
open.

MySQL 8.4 reference:
https://dev.mysql.com/doc/refman/8.4/en/set-transaction.html
https://dev.mysql.com/doc/refman/8.4/en/commit.html
