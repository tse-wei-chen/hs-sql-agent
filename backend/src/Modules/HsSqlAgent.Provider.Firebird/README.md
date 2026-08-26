# HsSqlAgent.Provider.Firebird

Firebird runtime provider for HsSqlAgent. Includes FirebirdSql.Data.FirebirdClient, Firebird SQL
lowering, connection creation, metadata discovery and provider-aware error mapping.

## Install

```bash
dotnet add package HsSqlAgent.Provider.Firebird
```

## Use

```csharp
using HsSqlAgent.Provider.Firebird;

var provider = new FirebirdProvider();
const string connectionString =
    "Database=localhost:/data/app.fdb;User=SYSDBA;Password=masterkey";

await using var connection = provider.CreateConnection(connectionString);
await connection.OpenAsync();

var schemas = await provider.GetSchemasAsync(connectionString);
var tables = await provider.GetTablesAsync(connectionString, "");
```

Use `HsSqlAgent.Server` instead when embedding the complete MCP server; it references this provider
automatically.

Project: https://github.com/tse-wei-chen/hs-sql-agent
