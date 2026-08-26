# HsSqlAgent.Server

Embeddable MCP SQL Agent server, administration API and web UI for ASP.NET Core.

## Install

```bash
dotnet add package HsSqlAgent.Server
```

## Minimal setup

```csharp
using HsSqlAgent.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHsSqlAgent(options =>
{
    options.AdminDatabaseProvider = "Sqlite";
    options.AdminConnectionString = "Data Source=hsagent.db";
    options.HmacSecretKey = builder.Configuration["HMAC_KEY"]!; // at least 32 bytes
    options.JwtSecretKey = builder.Configuration["JWT_KEY"]!;   // at least 32 bytes
    options.Mcp.PublicEndpoint = "http://localhost:8080/mcp";
});

var app = builder.Build();
app.UseHsSqlAgent();
app.Run();
```

Do not commit production keys. Supply `HMAC_KEY`, `JWT_KEY` and database credentials through your
deployment secret store. `UseHsSqlAgent()` applies the packaged migrations and maps the MCP and
administration endpoints.

The Server package brings in `HsSqlAgent.SqlCore`, provider abstractions and all six supported
providers: PostgreSQL, MySQL, SQLite, SQL Server, Oracle and Firebird.

Full configuration and deployment documentation:
https://github.com/tse-wei-chen/hs-sql-agent
