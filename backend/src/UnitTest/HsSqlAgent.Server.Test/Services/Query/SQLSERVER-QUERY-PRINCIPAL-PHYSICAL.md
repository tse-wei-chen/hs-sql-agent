# SQL Server 2022 — Query-only native principal boundary (#575)

SQL Server does **not** support the MySQL/PostgreSQL syntax
`SET TRANSACTION READ ONLY`. SQL Server T-SQL scalar UDFs ordinarily cannot
modify the database state, but ownership-chained SQL modules can read data
that the caller cannot select directly, and SQL CLR or external modules
have additional effects. A compiled SELECT, an MCP ReadOnly tool attribute,
or a missing Table Whitelist does **not** prove database least privilege.

## When the guard runs

`TypedQueryRuntime.ExecuteAsync` and `ExecuteWithFactsAsync` both call
`ExecuteVerifiedQueryAsync`. For SQL Server **and no Table Whitelist**
(`allowedTables == null`) the Host checks effective rights on the already
opened **native SqlConnection**, before calling the existing compiled
query executor. It never runs the SELECT if the probe fails, cannot use
a separate connection's identity as proof, and does not fall back to
an unchecked executor. Table-whitelisted SQL Server Query continues to
use the existing F# `RejectOpaqueNativeFunctions` policy.

The fixed, non-user-interpolated privilege probe checks:

- effective sysadmin/server and elevated database role membership;
- current DATABASE and SERVER effective grants using
  `sys.fn_my_permissions`, permitting a deliberately small set of
  metadata/read/connection permissions;
- database-wide `VIEW DEFINITION` (needed to avoid incomplete object
  catalogs caused by metadata visibility);
- schema-level ALTER/CONTROL/EXECUTE and per-object/column
  INSERT/UPDATE/DELETE/ALTER/CONTROL/TAKE OWNERSHIP;
- executable stored routines and user-defined SQL CLR assemblies;
- SQL Server synonyms (unproven cross-database/linked semantics).

A proof failure, missing/unknown evidence or database error prevents
the Query from executing. This does not change the F# SqlCore AST,
SqlCore capability contract, existing public CLR Query APIs, DML
approvals or registered MCP tool names.

## Example — independently provision the query reader

Deploy with a dedicated reader login and the DBA-approved table grants
rather than reusing `sa`, `db_owner` or application DML credentials.
This is an illustrative SQL Server administrator procedure; use an
independently managed secret and your own database/login names.

```sql
-- Run CREATE LOGIN against master with the intended credential policy:
CREATE LOGIN [agent_query_reader] WITH PASSWORD = '<managed-secret>';

-- Execute the following inside the intended application database:
CREATE USER [agent_query_reader] FOR LOGIN [agent_query_reader];
GRANT VIEW DEFINITION TO [agent_query_reader];
GRANT SELECT ON OBJECT::dbo.allowed TO [agent_query_reader];
-- Do NOT grant DML, EXECUTE, db_owner, db_datawriter, server admin roles,
-- object CONTROL/ALTER, or cross-database privileges.
```

Set the API key's database connection to that *reader* login.
A no-whitelist key has no compile-time table restrictions; the
physical database user's SELECT grants determine which relations are
readable. For table-scoped keys, configure an explicit Table Whitelist
**and** physical reader credentials wherever possible.

## Mandatory real provider evidence

`QueryNativeFunctionLiveTests.SqlServer_QueryOnlyNoWhitelist_RequiresPhysicallyReadOnlyPrincipal`
runs in the existing SQL Server 2022 service Job under
`QueryNativeFunctionLiveTests.SqlServer_` (missing live DB fails CI;
no silent skips).

It creates a fresh physical database with `dbo.allowed` and
`dbo.secret`, a separate SQL LOGIN and USER with only SELECT on allowed
plus metadata proof. Independent physical connections verify that:

1. A Query-only MCP key without a whitelist **cannot** use the
   privileged `sa` connection, even for an otherwise valid SELECT.
2. The limited SQL login can SELECT allowed; SQL Server denies a
   SELECT on the secret table.
3. A T-SQL `WITH EXECUTE AS OWNER` UDF can read the secret even on
   the limited reader login after an independent direct GRANT EXECUTE:
   a real security control, **not** a mocked function. While that
   EXECUTE grant exists, the MCP Query path fails closed even on an
   ordinary SELECT. REVOKE restores Query.
4. A single object-scoped `GRANT UPDATE` also blocks Query despite
   no database-wide write grant; `REVOKE` restores a normal SELECT.
5. Both tables retain their expected row count after all tests.

## Remaining security boundaries

This is a bounded *at-execution* principal authority check, **not** a
SQL Server engine-level read-only transaction or a proof against
permissions that change after the check, dangerous cross-database
module execution, external SQL CLR/OS/network effects, unsafe SQL Server
configuration, unsupervised linked servers, or all metadata visibility
edge cases. The probe deliberately denies synonyms and user-defined
assemblies rather than blessing opaque identities. An administrator
must separately provision and manage credentials and role separation.

No claim is made about SQL Server transparent row/column security,
unrestricted per-object UDF safety, physical pool size or other
providers. #575 and the 3.1.0 release gate remain OPEN.

SQL Server references:
- https://learn.microsoft.com/en-us/sql/relational-databases/system-functions/sys-fn-my-permissions-transact-sql
- https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration
- https://learn.microsoft.com/en-us/sql/relational-databases/user-defined-functions/create-user-defined-functions-database-engine
- https://learn.microsoft.com/en-us/sql/relational-databases/clr-integration/security/clr-integration-code-access-security
