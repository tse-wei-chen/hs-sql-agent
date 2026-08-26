# HsSqlAgent.Provider.MySql

MySQL runtime provider for HsSqlAgent. Includes the MySql.Data driver, MySQL SQL lowering,
connection creation, metadata discovery and provider-aware error mapping.

## Install

```bash
dotnet add package HsSqlAgent.Provider.MySql
```

## Use

```csharp
using HsSqlAgent.Provider.MySql;

var provider = new MySqlProvider();
const string connectionString =
    "Server=localhost;Port=3306;Database=app;User=root;Password=secret";

await using var connection = provider.CreateConnection(connectionString);
await connection.OpenAsync();

var schemas = await provider.GetSchemasAsync(connectionString);
var tables = await provider.GetTablesAsync(connectionString, "app");
```

Use `HsSqlAgent.Server` instead when embedding the complete MCP server; it references this provider
automatically.

Project: https://github.com/tse-wei-chen/hs-sql-agent
