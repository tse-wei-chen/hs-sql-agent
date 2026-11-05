# Backend test suites: ownership, layout, and verification

This is a map for the six backend xUnit projects. It is intentionally separate from the
SQL syntax corpus contract at [SqlAgent.Test/SyntaxCorpus/README.md](SqlAgent.Test/SyntaxCorpus/README.md).

## Where a test belongs

| Project | Scope | CI owner |
| --- | --- | --- |
| `SqlAgent.Test` | F# SqlCore compiler, six dialects, grammar/capabilities, DML coordinators and provider parity | `sqlagent-test`, `sqlagent-test-rest`, `sqlagent-syntax-matrix`, provider-specific jobs |
| `HsSqlAgent.Server.Test` | MCP/Host authorization, DML approvals/resume, DDL execution, HTTP/runtime and resource boundaries | `server-test`, `redis-approval-integration`, provider-specific DDL jobs |
| `Admin.Test` | Admin persistence, policies, lifecycle, migrations | `admin-test` |
| `Auth.Test` | Auth/session/role/token behavior | `auth-test` |
| `Infrastructure.Test` | Infrastructure adapters | `infrastructure-test` |
| `Common.Test` | Shared contracts | included via project build and applicable runners |

## SqlAgent.Test/Services/

The previous flat directory held **450 C# files**. All files were moved *byte-for-byte*
into domain directories; existing namespace `SqlAgent.Test.Services` and xUnit
`FullyQualifiedName` identity have been intentionally preserved.

- `Compiler/ParsingAndBinding`: parsing, lexical, identifier, AST, binding and schema checks.
- `Compiler/Safety`: compiler resource limits, diagnostics, evidence and semantic-drift guards.
- `Compiler/Capabilities`: provider-independent capability proof and SQL semantics.
- `Compiler/Dml`, `Compiler/Ddl`: mutation and schema compiler contracts.
- `Compiler/Dialects/{Postgres,MySql,SqlServer,Sqlite,Oracle,Firebird}`: dialect compiler contracts.
- `SqlCore/Dialects/<provider>/{Contracts,Live}`: provider-specific compilation proof vs physical/live parity.
- `SqlCore/{Scripts,Transactions,Schema,Query,Contracts,Live}`: cross-provider and pipeline contracts.
- `Grammar/{Positive,NegativeAndStress}`: grammar matrices, fuzzing and fail-closed syntax contracts.
- `Dml/{Approvals,Execution,Grammar,SchemaProof}`: approval and transaction coordinators, evidence and policy.
- `Providers`, `Query`, `Misc`: providers and supporting service contracts.

The grammar and script JSON sources stay in `SyntaxCorpus/` and `ScriptCorpus/`,
and their csproj copy/embed rules are unchanged.

## HsSqlAgent.Server.Test/Services/

The previous flat directory held **56 C# files**. Source namespaces and test names
were retained for all moved files.

- `Dml/{Approvals,Runtime}`: identities, durable approvals, Redis, replay and actual DML runtime.
- `Ddl/{Postgres,MySql,SqlServer,Sqlite,Oracle,Firebird}`: independent, provider-specific DDL execution.
- `SqlBoundary`: negative syntax and table-policy boundary matrices, including shared test support.
- `Query`: typed Query compiler/host boundary.
- `Operations`: throttling, metrics, health and resource limits.
- `Infrastructure`: supporting service integration.

MCP tool entry-point contract tests already live under `Tools/`; authorization
handler tests remain under `Authorization/`.

## How to add tests

1. Select the **behavior owner** first: SqlCore compilation or Host execution. Keep
   authentication/approval bypass tests in Host, even when SQL looks like a compiler case.
2. Choose a domain subdirectory. The CI layout guard rejects new `Services/*.cs`
   flat files for these two larger projects. Do not introduce another catch-all directory.
3. Keep the existing test class's namespace unless a deliberate migration updates all
   CI `FullyQualifiedName` filters. Do not change test identity during a folder-only move.
4. For language expansion, add positive and negative grammar cases and update corpus
   contracts rather than silently adjusting an existing floor. For injection, prove
   database **non-execution** and retain positive parameterized-literal cases.
5. Distinguish fast offline compiler tests from true database/provider parity. Real
   DB tests must state their fixture requirements and should not be accidentally pulled
   into a fast CI job. Do not use `Skip` to hide failing security contracts.

### Commands

```bash
python3 -m unittest discover -s .github/scripts -p 'test_test_suite_layout.py' -v
python3 .github/scripts/verify_test_suite_layout.py --check .
dotnet test --project backend/src/UnitTest/HsSqlAgent.Server.Test/HsSqlAgent.Server.Test.csproj
dotnet test --project backend/src/UnitTest/SqlAgent.Test/SqlAgent.Test.csproj
```

The canonical CI runner and provider exclusions are in
[.github/workflows/test.yml](../../../.github/workflows/test.yml).
Run the relevant provider-specific jobs as well; the two simple `dotnet test`
commands alone are not a replacement for the full CI matrix.

## Follow-up decomposition (not part of the folder-only migration)

- Move the 115 KB SQL Server composite CTE live-proof test into **focused proof
  fixtures** with shared container setup; split scenarios by proof invariant.
- Decompose the ~69 KB durable DML approval lifecycle test into pending, resume,
  replay, and crash/reconciliation suites while retaining one-time consumption evidence.
- Consolidate repeated provider factory/connection setup only where assertions remain
  transparent; avoid generic test helpers hiding the SQL, expected diagnostic stage, or
  physical database evidence.
- Review the ~87 KB strategy test class separately. This PR does not change fixture
  disposal, concurrency, xUnit collections, namespaces, or test behavior.
