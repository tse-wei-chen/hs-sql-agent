# AGENTS.md

## Repo Structure

- **Backend**: ASP.NET Core (net10.0) in `backend/src/`. Main entrypoint: `ToolBox/ToolBox.csproj`.
- **Frontend**: Nuxt 4 (pnpm) in `frontend/`. Source dir is `app/` (not default `app.vue` root).
- **Submodule**: `backend/src/Modules/SqlKata.Service` is a git submodule. Run `git submodule update --init --recursive` after clone.
- **Storage**: SQLite at runtime in `data/hsqlagent.db` (mounted via Docker volume).

## Commands

### Backend
```bash
# Run locally
cp backend/src/ToolBox/appsettings.Example.json backend/src/ToolBox/appsettings.json
dotnet run --project backend/src/ToolBox

# Run all tests (requires Docker for Testcontainers)
dotnet test backend/src/UnitTest/SqlAgent.Test/SqlAgent.Test.csproj
dotnet test backend/src/UnitTest/Admin.Test/Admin.Test.csproj

# Run single test
dotnet test backend/src/UnitTest/SqlAgent.Test/SqlAgent.Test.csproj --filter "FullyQualifiedName~SqliteStrategyTests"
```

### Frontend
```bash
cd frontend
pnpm install   # uses pnpm@10.22.0 (enforced via packageManager field)
pnpm dev       # http://localhost:3000, proxies /api to :8080
pnpm test      # vitest with happy-dom
pnpm generate  # static build to dist/ (used in Docker)
```

## Docker

```bash
cp .env.example .env   # set HMAC_KEY and JWT_KEY (32+ bytes each)
docker-compose up -d
```

Frontend is pre-built and served as static files from `ToolBox.dll`'s `wwwroot/`.

## Gotchas

- **Solution file**: `backend/hs-sql-agent.slnx` (slnx format, not classic .sln). Backend uses central package management via `Directory.Packages.props`.
- **Frontend aliases**: `@` and `~` both resolve to `frontend/app/` (see `vitest.config.ts`).
- **Testcontainers tests**: `SqlAgent.Test` spins up real DB containers (Sqlite, Postgres, MySql, SqlServer, Oracle, Firebird). Docker must be running.
- **Nuxt config**: `ssr: false`, dev proxy `/api/**` → `http://localhost:8080/api/**`.
- **Env vars**: Backend reads from both `appsettings.json` and environment variables (Docker Compose overrides).
- **`execute_dml_sql` tool signature**: Takes `string sql` from the caller. `McpServer server` and `CancellationToken cancellationToken` are injected by the MCP framework automatically (not caller-provided).

## DML Elicitation (Human-in-the-Loop)

`execute_dml_sql` uses **MCP Elicitation** (`McpServer.ElicitAsync`) to enforce human approval:

1. Server builds a **read-only impact preview** (affected-row count plus sample rows); it does not execute the mutation during preview.
2. Server issues a one-time typed approval challenge bound to the compiled plan, policy version, expiry and matched-row fingerprint, then calls `ElicitAsync()` so the MCP Client shows an interactive prompt to the human user.
3. User sees affected rows and decides Accept / Decline.
4. On acceptance, the server opens the commit transaction, re-queries the matched row identity set, verifies the approved plan/policy/row-set fingerprint, and executes the exact compiled mutation only if revalidation succeeds.

**Critical constraint**: The AI agent CANNOT bypass this flow. The tool handler blocks on `ElicitAsync()` and only resumes after the user responds through the client UI. There is no token-based caller-visible two-call workaround.

If the MCP client does not support Elicitation, the tool returns an error and refuses to execute any DML.
