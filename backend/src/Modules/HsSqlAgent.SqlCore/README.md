# HsSqlAgent.SqlCore

`HsSqlAgent.SqlCore` is the provider-driver-free SQL compiler layer extracted from HsSqlAgent.

It contains the structured SQL parser, AST, binding, normalization, semantic/capability validation,
provider-specific SQL lowering and query/DML compilation pipeline. It intentionally does not carry
ADO.NET database drivers, Dapper, MCP, ASP.NET Core, authentication or administration services.

The first package boundary preserves the existing `SqlAgent.Service.*` namespaces so the extraction
can be validated without mixing an assembly refactor with a public namespace migration.

Typical entry points include `CoreSqlTextParser`, `CoreSqlCompiler`, `CoreDmlCompiler` and
`SqlCapabilityMatrix`.

> This package is being extracted on an isolated refactor branch. Namespace cleanup and the final
> SqlKata fork distribution strategy are intentionally separate compatibility decisions.
