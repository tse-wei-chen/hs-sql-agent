# ⚙️ Configuration

## Environment Variables

hs-sql-agent is configured via environment variables (or `appsettings.json` for local development).

### Required Settings

| Variable    | Description                           | Example                             |
| ----------- | ------------------------------------- | ----------------------------------- |
| `HMAC_KEY`  | MCP HMAC secret key (minimum 32 bytes)| `YourMcpHmacSecretKeyHere-...`      |
| `JWT_KEY`   | JWT signing key (minimum 32 bytes)    | `YourSuperSecretKeyHere-...`        |

> [!IMPORTANT]
> Both keys **must be at least 32 bytes** (UTF-8 encoded). The application will fail to start if this requirement is not met.

### JWT Settings

| Variable                              | Default       | Description                    |
| ------------------------------------- | ------------- | ------------------------------ |
| `JWT_ISS`                             | `HS-Agent`    | JWT issuer                     |
| `JWT_AUD`                             | `HS-Agent-Users` | JWT audience                |
| `JWT_ACCESS_TOKEN_EXPIRATION_MINUTES` | `1`           | Access token lifetime (minutes)|
| `JWT_REFRESH_TOKEN_EXPIRATION_DAYS`   | `30`          | Refresh token lifetime (days)  |

### SqlConfig (Global Database Fallback)

| Variable | Default | Description |
| -------- | ------- | ----------- |
| (none)   | —       | Configured via `appsettings.json` or env vars |

A global `SqlConfig` section can be set in `appsettings.json` as a fallback database connection. Per-key connections in the Admin Panel override this.

```json
"SqlConfig": {
  "Provider": "Postgres",
  "ConnectionString": "Host=localhost;Port=5432;Database=mydb;Username=myuser;Password=mypassword"
}
```

Environment variable override:
- `SqlConfig__Provider`
- `SqlConfig__ConnectionString`

### Rate Limiting

| Variable                       | Default | Description                           |
| ------------------------------ | ------- | ------------------------------------- |
| `RATE_LIMITING_PERMIT_LIMIT`   | `0`     | Max requests per window (0 = disabled)|
| `RATE_LIMITING_WINDOW_SECONDS` | `0`     | Rate limit time window (0 = disabled) |
| `RATE_LIMITING_QUEUE_LIMIT`    | `0`     | Max queued requests                   |

### ASP.NET Core Settings

| Variable           | Default                | Description           |
| ------------------ | ---------------------- | --------------------- |
| `ASPNETCORE_URLS`  | `http://localhost:8080` | Server listen address |

## appsettings.json (Local Development)

For local development, create `backend/src/ToolBox/appsettings.json` from the example:

```bash
cp backend/src/ToolBox/appsettings.Example.json backend/src/ToolBox/appsettings.json
```

The actual file content:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  },
  "AllowedHosts": "*",
  "ASPNETCORE_URLS": "http://localhost:8080",
  "AppConnectionString": "Data Source=hsqlagent.db",
  "McpKeySettings": {
    "HmacSecretKey": "YourMcpHmacSecretKeyHere-AtLeast32Bytes!"
  },
  "JwtSettings": {
    "SecretKey": "YourSuperSecretKeyHere-AtLeast32Bytes!",
    "Issuer": "YourAppIssuer",
    "Audience": "YourAppAudience",
    "AccessTokenExpirationMinutes": 60,
    "RefreshTokenExpirationDays": 30
  },
  "SqlConfig": {
    "Provider": "Postgres",
    "ConnectionString": "Host=localhost;Port=5432;Database=mydb;Username=myuser;Password=mypassword"
  },
  "RateLimiting": {
    "PermitLimit": 0,
    "WindowSeconds": 0,
    "QueueLimit": 0
  }
}
```

## Docker Compose

For Docker deployment, all settings are configured via environment variables in `docker-compose.yml`:

```yaml
services:
  hs-sql-agent:
    image: ghcr.io/tse-wei-chen/hs-sql-agent:latest
    container_name: hs-sql-agent
    ports:
      - "8080:8080"
    volumes:
      - ./data:/app/data
    environment:
      - AppConnectionString=Data Source=/app/data/hsqlagent.db
      - McpKeySettings__HmacSecretKey=${HMAC_KEY}
      - JwtSettings__SecretKey=${JWT_KEY}
      - JwtSettings__Issuer=${JWT_ISS}
      - JwtSettings__Audience=${JWT_AUD}
      - JwtSettings__AccessTokenExpirationMinutes=${JWT_ACCESS_TOKEN_EXPIRATION_MINUTES}
      - JwtSettings__RefreshTokenExpirationDays=${JWT_REFRESH_TOKEN_EXPIRATION_DAYS}
      - RateLimiting__PermitLimit=${RATE_LIMITING_PERMIT_LIMIT}
      - RateLimiting__WindowSeconds=${RATE_LIMITING_WINDOW_SECONDS}
      - RateLimiting__QueueLimit=${RATE_LIMITING_QUEUE_LIMIT}
    restart: unless-stopped
```

## Configuration Precedence

1. Environment variables (highest priority)
2. `appsettings.json` (local development only)
3. Application defaults (lowest priority)

Environment variable names use double-underscore (`__`) as a separator for nested configuration keys (e.g., `JwtSettings__SecretKey`).
