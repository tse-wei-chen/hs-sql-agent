# HsSqlAgent.Provider.PostgreSql

PostgreSQL runtime provider for HsSqlAgent. Includes the Npgsql driver, PostgreSQL SQL lowering,
connection creation, metadata discovery and provider-aware error mapping.

## Install

```bash
dotnet add package HsSqlAgent.Provider.PostgreSql
```

## Use

```csharp
using HsSqlAgent.Provider.PostgreSql;

var provider = new PostgresProvider();
const string connectionString =
    "Host=localhost;Port=5432;Database=app;Username=postgres;Password=secret";

await using var connection = provider.CreateConnection(connectionString);
await connection.OpenAsync();

var schemas = await provider.GetSchemasAsync(connectionString);
var tables = await provider.GetTablesAsync(connectionString, "public");
```

Use `HsSqlAgent.Server` instead when embedding the complete MCP server; it references this provider
automatically.

Project: https://github.com/tse-wei-chen/hs-sql-agent
