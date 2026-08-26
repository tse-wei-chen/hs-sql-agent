# HsSqlAgent.Provider.Oracle

Oracle Database runtime provider for HsSqlAgent. Includes Oracle.ManagedDataAccess.Core, Oracle SQL
lowering, connection creation, metadata discovery and provider-aware error mapping.

## Install

```bash
dotnet add package HsSqlAgent.Provider.Oracle
```

## Use

```csharp
using HsSqlAgent.Provider.Oracle;

var provider = new OracleProvider();
const string connectionString =
    "User Id=app;Password=secret;Data Source=localhost:1521/FREEPDB1";

await using var connection = provider.CreateConnection(connectionString);
await connection.OpenAsync();

var schemas = await provider.GetSchemasAsync(connectionString);
var tables = await provider.GetTablesAsync(connectionString, "APP");
```

Use `HsSqlAgent.Server` instead when embedding the complete MCP server; it references this provider
automatically.

Project: https://github.com/tse-wei-chen/hs-sql-agent
