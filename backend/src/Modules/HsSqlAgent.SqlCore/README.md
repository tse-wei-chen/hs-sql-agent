# HsSqlAgent.SqlCore

Provider-driver-free SQL parsing, validation and compilation for HsSqlAgent.

It contains the structured SQL parser, AST, binding, normalization, semantic/capability validation,
provider-specific SQL lowering and query/DML compilation pipeline. It intentionally does not carry
ADO.NET database drivers, Dapper, MCP, ASP.NET Core, authentication or administration services.

## Install

```bash
dotnet add package HsSqlAgent.SqlCore
```

## Compile SQL

```csharp
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;

var parsed = CoreSqlTextParser.ParseQuery(
    "SELECT id, name FROM users WHERE active = true",
    SqlAgentToolType.Postgres);

var command = CoreSqlCompiler.CreateDefault().Compile(
    parsed,
    SqlAgentToolType.Postgres,
    new SqlPlanValidationContext("app-policy-v1"),
    new SqlExecutionPlanPolicy(QueryMaxRows: 100));

Console.WriteLine(command.Sql);
foreach (var parameter in command.Parameters)
    Console.WriteLine($"{parameter.Name} = {parameter.Value}");
```

For mutations, use `CoreSqlTextParser.ParseDml` with `CoreDmlCompiler`. Compilation produces an
immutable `CompiledSqlCommand`; this package does not open database connections or execute SQL.

## Package boundaries

- Use `HsSqlAgent.Provider.*` when you also need an ADO.NET driver, metadata discovery and
  provider-specific runtime behavior.
- Use `HsSqlAgent.Server` to embed the complete MCP SQL Agent in ASP.NET Core.
- The bundled SqlKata fork is an internal implementation detail and requires no separate package.

Project: https://github.com/tse-wei-chen/hs-sql-agent
