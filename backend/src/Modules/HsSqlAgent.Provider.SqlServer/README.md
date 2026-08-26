# HsSqlAgent.Provider.SqlServer

Microsoft SQL Server runtime provider for HsSqlAgent. Includes Microsoft.Data.SqlClient, SQL Server
lowering, connection creation, metadata discovery and provider-aware error mapping.

## Install

```bash
dotnet add package HsSqlAgent.Provider.SqlServer
```

## Use

```csharp
using HsSqlAgent.Provider.SqlServer;

var provider = new MsSqlServerProvider();
const string connectionString =
    "Server=localhost;Database=app;User Id=sa;Password=secret;TrustServerCertificate=True";

await using var connection = provider.CreateConnection(connectionString);
await connection.OpenAsync();

var schemas = await provider.GetSchemasAsync(connectionString);
var tables = await provider.GetTablesAsync(connectionString, "dbo");
```

Use `HsSqlAgent.Server` instead when embedding the complete MCP server; it references this provider
automatically.

Project: https://github.com/tse-wei-chen/hs-sql-agent
