# 🐳 Deployment

## Docker Deployment (Recommended)

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/)
- [Docker Compose](https://docs.docker.com/compose/install/)

### Steps

1. **Clone the repository**

```bash
git clone https://github.com/tse-wei-chen/hs-sql-agent.git
cd hs-sql-agent
```

2. **Configure environment**

```bash
cp .env.example .env
```

Edit `.env` with strong, unique secrets:

```env
HMAC_KEY=<your-32+byte-hmac-key>
JWT_KEY=<your-32+byte-jwt-key>
```

3. **Launch**

```bash
docker compose up -d
```

4. **Verify**

```bash
docker compose logs -f
```

### Data Persistence

- SQLite database is stored at `./data/hsqlagent.db` (mounted volume)
- This volume persists across container restarts

### Updating

```bash
docker compose pull
docker compose up -d
```

## Docker Image

The official image is published to GitHub Container Registry:

```
ghcr.io/tse-wei-chen/hs-sql-agent:latest
```

### Image Build Process

The Dockerfile uses a multi-stage build:

1. **frontend-builder** — builds Nuxt SPA with `pnpm generate`
2. **backend-builder** — builds and publishes .NET application, copies frontend `dist/` to `wwwroot/`
3. **runtime** — minimal ASP.NET runtime image

## Production Considerations

### Security

- **Never use default/example keys** in production
- Use a secrets manager or secure environment variable injection
- Consider exposing the service behind a reverse proxy (e.g., Nginx, Traefik)

### Networking

- Default port: `8080`
- The MCP endpoint is at `/mcp`
- The Admin Panel is served as static files at `/`
- REST API is at `/api/*`

### Reverse Proxy Example (Nginx)

```nginx
server {
    listen 443 ssl;
    server_name your-domain.com;

    location / {
        proxy_pass http://localhost:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### Resource Recommendations

| Scenario    | CPU   | Memory | Storage |
| ----------- | ----- | ------ | ------- |
| Development | 1 vCPU| 512MB  | 1GB     |
| Production  | 2 vCPU| 2GB    | 10GB    |

## Health Checks

The application exposes health information through:
- HTTP 200 response at the root path (`/`)
- MCP endpoint at `/mcp`

## Monitoring

- **Docker logs**: `docker compose logs -f`
- **Audit Logs**: Available in the Admin Panel under **Audit Logs**
- **Dashboard**: Real-time monitoring in the Admin Panel
