# HsSqlAgent.Provider.Sqlite

SQLite runtime provider for HsSqlAgent. Includes Microsoft.Data.Sqlite, SQLite SQL lowering,
connection creation, metadata discovery and provider-aware error mapping.

## Install

```bash
dotnet add package HsSqlAgent.Provider.Sqlite
```

## Use

```csharp
using HsSqlAgent.Provider.Sqlite;

var provider = new SqliteProvider();
const string connectionString = "Data Source=app.db";

await using var connection = provider.CreateConnection(connectionString);
await connection.OpenAsync();

var tables = await provider.GetTablesAsync(connectionString, "main");
var columns = await provider.GetColumnsAsync(connectionString, "main", "users");
```

Use `HsSqlAgent.Server` instead when embedding the complete MCP server; it references this provider
automatically.

Project: https://github.com/tse-wei-chen/hs-sql-agent
