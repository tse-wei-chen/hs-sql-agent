# Oracle 26ai / Firebird 5 — native read-only Query boundary (#575)

## The gap

A SELECT AST, an MCP tool marked `ReadOnly`, and a Query-only API key do not
prove that server-native SQL or an invoked routine cannot write. Unlike
PostgreSQL and MySQL, the existing Oracle/Firebird `TypedQueryRuntime` paths
previously executed the compiled query without a physical read-only transaction.

## Host behavior

For **both** `ExecuteAsync` and `ExecuteWithFactsAsync`, after opening the
physical connection, verifying its provider version and compiling the Query,
`ExecuteVerifiedQueryAsync` now:

1. Requires `IProviderDmlPreviewTransactionSource` for Oracle or Firebird;
   missing support fails closed instead of falling back to an unchecked executor.
2. Reuses the provider's existing DML-preview transaction factory **on the same
   open connection**. Oracle uses a native transaction plus
   `SET TRANSACTION READ ONLY`; Firebird uses `FbTransactionOptions` with a
   native read-only transaction parameter block (`FbTransactionBehavior.Read`).
3. Binds the validated, immutable compiled command to **that** transaction,
   runs the existing Query executor, then rolls back/disposes the transaction.
   A failure to enter the read-only mode never runs the SELECT unfenced.

There is no new SQL parser, MCP tool, F# facade or public Query API. The
existing PostgreSQL search-path/transaction fence, MySQL next-transaction
READ ONLY fence, SQLite function-registry check and SQL Server principal
attestation are unchanged. DML approval/commit behavior is unchanged.

## Test-first and mandatory physical evidence

The four `TypedQueryRuntimeTests.ExecuteQuery_OracleAndFirebird_RequiresNativeReadOnlyTransaction`
cases use a fail-fast factory marker to establish that **both** Query entrypoints
must invoke the native read-only source before any SQL command for each
provider. The original nontransactional paths would never reach that factory.

Existing `QueryNativeFunctionLiveTests.Oracle_...` and
`QueryNativeFunctionLiveTests.Firebird_...` tests now also verify:

- A new real Oracle 26ai or Firebird 5 connection enters the **production**
  provider-native read-only transaction and the database rejects an actual
  `INSERT` in that transaction.
- A published `execute_query_sql` call with **no** table whitelist can still
  read the allowed test table; the same call goes through the shared Query
  runtime path and thus the read-only native transaction.
- The existing qualified/quoted native-function policy blocks hidden-table
  reads, and independent physical row counts remain unchanged.

**Oracle DDL snapshot constraint:** Oracle may return `ORA-01466` when a
`SET TRANSACTION READ ONLY` snapshot reads a table just created/altered in
the same live test. The Oracle fixture first uses an independent, bounded
read-only compiled Query to establish post-DDL snapshot visibility; it
retries **only** that specific error (up to 30 attempts, 1 second apart).
The Host does **not** retry arbitrary user SQL, run outside a read-only
transaction, or claim that reads racing with physical DDL always succeed.
The production path remains fail-closed on `ORA-01466`.

The existing real-provider CI service steps select tests by
`QueryNativeFunctionLiveTests.Oracle_` and
`QueryNativeFunctionLiveTests.Firebird_`. Missing live-provider connections
are CI failures, not silent skips. The resulting PR is mergeable only after
**18/18 Tests jobs** and the SqlCore baseline comparison pass on the same
final commit SHA.

## Remaining limitations and release status

This is a **transaction-local write fence**, not a full proof that native
functions are side-effect-free. In particular, Oracle autonomous transactions,
Firebird autonomous transactions, UDF external effects, permission escalation,
name resolution/shadowing, cross-database identity, missing table whitelists,
and grants to non-query objects still need independent validation or
least-privilege database accounts. The approved DB login still determines
physical data visibility; no whitelist means no compiled per-table restriction.

Do **not** close #575, assert complete six-provider Query isolation, or
publish v3.1.0 based on this bounded improvement. Real transport
disconnect/crash/failover, distributed admission and final dependency/CVE
release evidence also remain open.

References:
- [Oracle: transaction modes](https://docs.oracle.com/en/database/oracle/oracle-database/23/sqlrf/SET-TRANSACTION.html)
- [Firebird: transaction parameter buffer](https://www.firebirdsql.org/file/documentation/html/en/refdocs/fblangref50/firebird-50-language-reference.html)
