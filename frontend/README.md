# hs-sql-agent Admin UI

The Nuxt 4 frontend for hs-sql-agent 3.0.0. Application source lives in `app/`;
both `@` and `~` resolve to that directory.

## Development

Run these commands from `frontend/`:

```bash
pnpm install
pnpm dev
```

Open <http://localhost:3000>. Start the backend on port 8080; the development
server proxies `/api/**`, `/mcp`, and `/mcp/**` to it.

## Validation

```bash
pnpm test --run
pnpm lint
```

Tests use Vitest with happy-dom.

## Static deployment

```bash
pnpm generate
```

The generated SPA is written to `dist/`. The Docker build copies it into the
backend's `wwwroot/`, where ASP.NET Core serves it.

See the [repository README](../README.md) for configuration and deployment.
