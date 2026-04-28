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
