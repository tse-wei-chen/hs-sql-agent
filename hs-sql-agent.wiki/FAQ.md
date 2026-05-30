# ❓ FAQ

## General

### What is hs-sql-agent?
hs-sql-agent is a high-performance MCP (Model Context Protocol) server that bridges AI agents with relational databases. It provides deterministic SQL generation, enterprise governance (access control, audit logging, rate limiting), and a built-in Admin Panel.

### What databases are supported?
SQLite, PostgreSQL, MySQL, SQL Server, Oracle, and FireBird.

### Do I need to know SQL to use it?
No. The AI agent handles SQL generation through structured parameters. The agent discovers database schemas via MCP tools and constructs queries deterministically.

---

## Setup

### How do I get started?
1. Run `docker compose up -d` with a configured `.env` file
2. Open the Admin Panel at `http://localhost:8080`
3. Create an admin account
4. Add a database connection in **DB Management**
5. Issue an MCP key in **MCP Keys**
6. Configure your AI client with the MCP key

### Can I run without Docker?
Yes, for local development. See the [Development guide](Development).

### What ports are used?
- `8080`: Main application (Admin Panel + MCP endpoint)
- `3000`: Frontend dev server (local development only)

---

## MCP & AI Clients

### Which clients are supported?
Claude Desktop, VS Code (Cline/Roo), Cursor, and any MCP-compatible client.

### How do I configure my client?
Add the MCP server configuration with the URL `http://localhost:8080/mcp` and `X-MCP-Server-Key` header.

### My key was shown only once — what if I lose it?
You'll need to issue a new key. Keys can be revoked from the Admin Panel at any time.

---

## Security

### How are connection strings secured?
Database connection strings are encrypted at rest using AesGcm encryption.

### How is SQL injection prevented?
All SQL is generated through SqlKata, which enforces automatic parameterization. LLM inputs are never concatenated into raw SQL.

### Can I restrict access to specific tables?
Yes. The **Table Whitelist** feature in the Admin Panel lets you control which tables each key can access.

---

## Performance

### Is this suitable for production?
The project is in alpha stage but includes production guardrails like rate limiting, audit logging, and access control. Evaluate based on your requirements.

### How does it handle high concurrency?
The backend uses:
- ASP.NET Core on .NET 10.0 for high throughput
- SHA256-hashed cache keys with stripe locking
- Background services for non-blocking audit writes

---

## Development

### How do I run tests?
```bash
# Backend unit tests
dotnet test backend/src/UnitTest/Admin.Test/Admin.Test.csproj

# Backend integration tests (requires Docker)
dotnet test backend/src/UnitTest/SqlAgent.Test/SqlAgent.Test.csproj

# Frontend tests
cd frontend && pnpm test
```

### Can I add custom MCP tools?
Yes. Use the **Custom SQL Tools** feature in the Admin Panel to define parameterized SQL operations that are automatically exposed as MCP tools.

### How do I contribute?
See the [Contributing guide](https://github.com/tse-wei-chen/hs-sql-agent?tab=contributing-ov-file#contribution-guidelines).

---

## Troubleshooting

### The application won't start
Check that:
- `HMAC_KEY` and `JWT_KEY` are at least 32 bytes
- The SQLite data directory has write permissions
- Port 8080 is not in use

### Connection test fails
Verify:
- Database server is reachable from the container
- Connection string is correct
- Firewall allows the connection

### MCP tools not showing up in my client
Check:
- The API key is valid and not revoked
- The key has allowed tools configured
- The MCP URL is correct (`http://host:8080/mcp`)
- The `X-MCP-Server-Key` header is set

### Audit logs are empty
Audit logs are only written when MCP tools are executed. Try running a query first.
