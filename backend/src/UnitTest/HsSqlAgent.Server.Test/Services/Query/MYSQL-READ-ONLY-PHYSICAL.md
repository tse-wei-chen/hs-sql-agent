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


## #575 / #615 — MySQL session TEMPORARY table side effects through Query-only native functions

**Do not extrapolate from the ordinary InnoDB write fence.** MySQL 8.4
`SET TRANSACTION READ ONLY` permits writes to session-local `TEMPORARY`
tables. `MEMORY` tables are nontransactional, so their writes may also remain
visible in the same server session after a `ROLLBACK`. A no-whitelist Query
key is not independent authorization to execute opaque database functions.

**Initial falsified hypothesis:** a first test-only CI experiment attempted
to `CREATE TEMPORARY TABLE` from inside a stored function called under
`READ ONLY`. The native MySQL 8.4 engine returned `Cannot execute statement
in a READ ONLY transaction` before the application's Query guard was
exercised. That test failure was **not** counted as a behavioral RED and
cannot be used as evidence of an exploit.

**Corrected real native experiment:** instead, create the temporary table
*before* the transaction on the same connection, then call a qualified stored
function that inserts into the existing temporary table.

1. A real `READ ONLY` MySQL 8.4 native transaction invokes
   `database.temp_side_effect(id)` and writes one row to a
   `TEMPORARY ENGINE=MEMORY` table.
2. The session still sees that row after `ROLLBACK` (independent positive
   proof of a nontransactional physical effect).
3. The official MySql.Data provider's real pool with explicitly disabled
   `ConnectionReset` and pool size 1 reuses the same `CONNECTION_ID()`
   with that temporary table still present.
4. The published Query-only MCP tool, with no Table Whitelist, originally
   accepted the native function and did not refuse the unsafe Query.
   [Test-only RED CI run #42998106503](https://github.com/tse-wei-chen/hs-sql-agent/actions/runs/42998106503)
   fails at the concrete published-MCP safety assertion **after** all the
   native control and same-session checks passed; **2 other mandatory
   MySQL native-function integration cases passed, 0 skipped**.
5. **Only physical Host Query** opts into the existing closed
   `RejectOpaqueNativeFunctions(MySQL)` policy via the typed
   `SqlExecutionPlanPolicy.RequireProvenMySqlNativeFunctions` signal when
   the table whitelist is absent. A qualified/quoted MySQL native
   function rejects at Policy with `SQL_POLICY_NATIVE_QUERY_FUNCTION_NOT_PROVEN`,
   before any SQL is executed. The default standalone `SqlCoreFacade`
   compiler **retains** native function grammar and its existing public
   one-argument execution-policy constructor. Ordinary unqualified core
   built-ins remain allowed for the Host; no provider grammar is removed.
6. A unit contract covers the compiler and compiler-with-facts paths,
   while the original SQL Server unrestricted native-function behavior
   remains unchanged. The dedicated live MySQL 8.4 CI validates the
   compiled published MCP path with real temp session effects.

**Boundary:** This selectively refuses **qualified/quoted** native
MySQL function names on no-whitelist **Host Query**, while standalone SqlCore
compilation remains opt-in and full-fidelity. Unknown/unqualified function
resolution, external native UDFs, routines reached by views or different
execution identities, functions that open another connection and any
privileged definer effects remain unproven. It is not a general MySQL
read-only sandbox or permission proof. Hosts must use database accounts
with least privilege and restrict install/EXECUTE of unsafe native
functions. #575 remains open and 3.1.0 publication stays blocked.

References:
- https://dev.mysql.com/doc/refman/8.4/en/commit.html
- https://dev.mysql.com/doc/refman/8.4/en/create-temporary-table.html
- https://dev.mysql.com/doc/connector-net/en/connector-net-8-0-connection-options.html
