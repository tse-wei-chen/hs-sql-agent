# 🛠️ Development

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- [pnpm](https://pnpm.io/) — version 10.22.0 enforced
- [Docker](https://www.docker.com/) (for integration tests with Testcontainers)

## Clone & Setup

```bash
git clone https://github.com/tse-wei-chen/hs-sql-agent.git
cd hs-sql-agent
git submodule update --init --recursive
```

## Backend

### Configuration

```bash
cp backend/src/ToolBox/appsettings.Example.json backend/src/ToolBox/appsettings.json
```

Edit `appsettings.json` to configure your database connection and secrets.

### Run

```bash
dotnet run --project backend/src/ToolBox
```

The backend runs at `http://localhost:8080`.

### Tests

```bash
# Run all tests
dotnet test backend/src/UnitTest/SqlAgent.Test/SqlAgent.Test.csproj
dotnet test backend/src/UnitTest/Admin.Test/Admin.Test.csproj

# Run a single test (xUnit v3)
dotnet test backend/src/UnitTest/SqlAgent.Test/SqlAgent.Test.csproj --filter "FullyQualifiedName~SqliteStrategyTests"
```

> **Note**: SqlAgent tests require Docker for Testcontainers (spins real containers for Postgres, MySQL, SQL Server, Oracle, FireBird). SQLite tests use in-memory SQLite (no container needed).

### Solution Structure

The solution file uses the `.slnx` format (not classic `.sln`):

```
backend/hs-sql-agent.slnx
```

Central package management is in `backend/Directory.Packages.props`. All NuGet package versions are defined there.

## Frontend

### Install

```bash
cd frontend
pnpm install
```

### Dev Server

```bash
pnpm dev
```

The frontend dev server runs at `http://localhost:3000` with API proxy at `/api/**` → `http://localhost:8080/api/**`.

### Build

```bash
pnpm generate   # Static build to dist/ (used in Docker)
```

### Tests

```bash
pnpm test   # Vitest with happy-dom
```

### Key Frontend Conventions

| Convention | Detail |
| ---------- | ------ |
| Source dir | `app/` (not default root) |
| Aliases    | `@` and `~` → `frontend/app/` |
| SSR        | Disabled (`ssr: false`) |
| UI Library | shadcn-vue (style: nova, base: reka) |
| Styling    | Tailwind CSS 4 |
| Forms      | VeeValidate |
| HTTP       | Xior (axios-like) |

## Docker Build

```bash
docker compose up -d
```

The Dockerfile is a **multi-stage build**:

1. **frontend-builder**: Node 24 Alpine, `pnpm generate` → `dist/`
2. **backend-builder**: .NET SDK 10.0, copies frontend `dist/` → `wwwroot/`, publishes `ToolBox.dll`
3. **runtime**: ASP.NET 10.0 runtime, exposes 8080, runs `ToolBox.dll`

## Code Style

- **Tabs** for most files
- **4-space** indentation for `.cs` files
- **2-space** indentation for `.vue`/`.ts`/`.yml`
- See `.editorconfig` at the project root

## Testing Strategy

| Project       | Framework     | Type              | Requires Docker |
| ------------- | ------------- | ----------------- | --------------- |
| Admin.Test    | xUnit v3+Moq  | Unit tests        | ❌              |
| SqlAgent.Test | xUnit v3      | Integration tests | ✅ (except SQLite)|

Code coverage exclusions (in `Directory.Build.props`):
- `[*]*.Models.*`
- `**/Modules/SqlKata.Service/**/*`

## Environment Variables

The backend reads configuration from `appsettings.json` + environment variables. Docker Compose overrides via environment variables:

| Variable | Description |
| -------- | ----------- |
| `HMAC_KEY` | MCP HMAC secret key (32+ bytes) |
| `JWT_KEY` | JWT signing key (32+ bytes) |
| `JWT_ISS` | JWT issuer |
| `JWT_AUD` | JWT audience |
| `JWT_ACCESS_TOKEN_EXPIRATION_MINUTES` | Access token TTL |
| `JWT_REFRESH_TOKEN_EXPIRATION_DAYS` | Refresh token TTL |
| `RATE_LIMITING_PERMIT_LIMIT` | Max requests per window |
| `RATE_LIMITING_WINDOW_SECONDS` | Rate limit window |
| `RATE_LIMITING_QUEUE_LIMIT` | Queue size for rate limiter |
| `SqlConfig__Provider` | Global database provider fallback |
| `SqlConfig__ConnectionString` | Global database connection string fallback |
