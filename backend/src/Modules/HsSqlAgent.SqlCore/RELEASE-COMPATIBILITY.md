# SqlCore v3.1 release compatibility and migration evidence

**Scope:** the published **HsSqlAgent.SqlCore 3.1.0** package versus an **unpublished `develop` PR candidate**. The repository's `v3.1.0` tag points to `c077f5703f2c0c8e6446a6e6017f3d375523976b`. A passing develop PR does not publish a NuGet package, advance `main`, or claim every new capability is available in the existing 3.1.0 package.

## What each API can and cannot authorize

| Contract | Output and supported intent | Not an execution grant |
| --- | --- | --- |
| `ParseScript` | Bounded syntax, source spans, statement family, batch boundaries | No semantic proof, SQL execution or privileges |
| `AnalyzeScript` | Static catalog/transaction/session/effect hypotheses, typed dependency edges and composition findings on current develop | No live row-set, native rollback, independent approval or Host Script authorization |
| `CompileQuery` / `TryCompileQuery` | Bounded Query compilation under provider/version, policy and function restrictions | No physical DB privileges, View/UDF expansion or authenticated MCP access |
| `CompileDml` / `TryCompileDml` | Supported mutation into immutable compiled commands | No approval challenge or authority to issue a COMMIT |
| `CompileDdl`, `CompileDdlBatch`, `PreviewDdl` | Explicitly supported, provider-specific schema plans and static expected diffs | No live schema proof, feature enablement or independent DDL approval |

All six dialects—PostgreSQL, MySQL, SQL Server, SQLite, Oracle and Firebird—have their own version and topology boundaries. A parsed grammar construct is **not** automatically supported in compilation or allowed by the Host.

The newer U2–U4 `StaticDependencyEdges`, `DmlEffectReconciliations`, `CompositionStatus` and `IsCompositionExecutionProof` evidence is **develop-only until separately released**. The last property is always `false`: static effects do not enable TCL execution. U8's Query bound named-source comparison is likewise a compiler consistency check, **not** a complete live physical ReadSet or table authorization.

## C# consumer examples (no DB access)

```csharp
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Core.Pipeline;

var provider = SqlAgentToolType.Postgres;
var parsed = SqlCoreFacade.ParseScript("SELECT 1;", provider);
Console.WriteLine(parsed.Statements[0].Kind); // Query

var rejected = SqlCoreFacade.TryCompileQuery(
    "DELETE FROM orders WHERE id = 7",
    provider, provider,
    new SqlPlanValidationContext("v3-migration-policy"),
    new SqlExecutionPlanPolicy(10));
Console.WriteLine(rejected.Success); // False: a mutation is not Query
Console.WriteLine(rejected.ErrorCode); // Typed error code; do not depend on message text
```

For a supported query, choose explicit `SqlPlanValidationContext`, `SqlExecutionPlanPolicy`, provider versions and trusted table identities. The compiler never opens a DB connection and never replaces least-privileged DB credentials.

## Existing ASP.NET Core / MCP Host sample

Install **`HsSqlAgent.Hosting`** rather than assuming the compiler package exposes a server or UI. The standard host is already implemented by [`backend/src/ToolBox/Program.cs`](../../ToolBox/Program.cs):

```csharp
using HsSqlAgent.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddHsSqlAgentStandardHost();

var app = builder.Build();
app.UseHsSqlAgentStandardHost();
await app.RunAsync();
```

The real [`ToolBox appsettings example`](../../ToolBox/appsettings.Example.json) is the configuration reference. Supply three **independent**, randomly generated Base64-encoded 32-byte secrets via protected configuration for `SecretProtection:DatabasePasswords`, `SecretProtection:DmlPayloads` and `SecretProtection:DmlFingerprintKey`. The example values are not production secrets. The [Hosting README](../HsSqlAgent.Hosting/README.md) shows both `DmlApproval:Provider=McpElicitation` (default) and the optional `Webhook` endpoint/callback/signing configuration.

The four metadata/Query MCP tools remain selected by default. DML and DDL are **not** auto-selected or entitled by an empty explicit tool-selection list. DDL has a separate opt-in feature gate and approval provider, and unknown DML/DDL COMMIT outcomes are non-retryable without physical reconciliation.

For deleting an order and its details as a single unit, supply a **supported DML-only batch**, subject to exact row-set approval, through the already-existing `execute_dml_sql`:

```sql
DELETE FROM order_details WHERE order_id = 7;
DELETE FROM orders WHERE id = 7;
```

The **Host** owns the native transaction, cancellation/lease, one-use approval, row-set recheck and COMMIT. Do **not** add caller-authored `BEGIN`, `SAVEPOINT`, or `COMMIT` to this tool. U6 explicitly decided **NO-GO** on an arbitrary executable TCL Script API; Script analysis remains Analyze-only.

## Release ABI validation (U9 gate)

The PR's `v3-contract-and-abi` job now requires **both**:

1. The existing exact `develop` base → PR head CLR-signature, SQL classification, diagnostics and compile-output parity, using the identical benchmark harness.
2. A **separate published-package comparison**: resolve immutable `v3.1.0` to the pinned commit, download the actual `HsSqlAgent.SqlCore` **3.1.0 NuGet package** from NuGet.org, record its SHA-256, confirm net8.0 and net10.0 assemblies are present in both packages, then compare the published package to the freshly packed candidate with Microsoft's `ApiCompat` tool. The same comparison also enforces unchanged public parameter names.

This binary gate rejects incompatible published API changes even when PR base→head looks compatible. It is stronger evidence than cloning the tag and inspecting source text; it is *not* a claim of runtime compatibility for arbitrary consumers, behaviorally changed SQL, or untested dependent packages. Cross-package release checks remain owned by the separate release preflight.

## Performance and code delta evidence (U9 gate)

The existing U0 workflow executes a fixed six-dialect, eight-family ParseScript/Compile corpus twice on the **same runner** (exact PR base and head, identical harness, .NET 10 Release). It records p50 and p95 microseconds, mean allocated bytes per operation and output digests. The U9 report pairs corresponding cases and captures the exact SHAs; output parity remains a hard requirement.

A single run with 10 warmups and 40 measured attempts is **observational**. A >5% ratio warns for inspection; it is not a reproducible regression on a shared GitHub runner. Repeat on stable hardware before using a 10% threshold for performance decisions. Do not invent token-scan counts or AST walk counts; the existing benchmark records those as not directly measured.

The U9 code-delta report uses exact Git base/head commits and separates production, tests, generated migrations and documentation. LOC by itself does not prove safety or execution speed. New SQL grammar, a new Script Execution tool, new runtime abstraction, package publication and automatic release are explicitly **out of scope**.

## Migration / rollback checklist

1. Keep the already-published `3.1.0` package in production while reviewing any new develop-only analysis fields; do not imply the released package already has U2–U8 features.
2. Verify both target frameworks, public CLR API and application-specific SQL syntax on the exact release candidate before upgrading.
3. Upgrade host configuration deliberately: persist the three protected key sources, verify Admin Store migration, DML approval provider and least-privileged DB principals, and retain four default read/query tools.
4. Treat any `outcome_unknown` as non-retryable; reconcile physical state, approval request ID and audit before a new request.
5. If any release/security/ABI/live-database gate fails, **stop promotion**. Preserve the already-published package; never silently downgrade compiler/approval safety rules or force-merge `main`.

Detailed deployment, schema support and provider-specific restrictions remain in the [documentation site](https://sql-agent.net/en/docs/) and the corresponding repository READMEs.
