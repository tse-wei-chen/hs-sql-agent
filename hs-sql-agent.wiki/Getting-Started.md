# 🚀 Getting Started

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) & [Docker Compose](https://docs.docker.com/compose/install/)

## Quick Start (Docker Compose)

The easiest way to run hs-sql-agent is using Docker Compose. This ensures your configuration is saved and your data persists across restarts.

### 1. Clone the Repository

```bash
git clone https://github.com/tse-wei-chen/hs-sql-agent.git
cd hs-sql-agent
```

### 2. Setup Configuration

Copy the example environment file:

```bash
cp .env.example .env
```

Edit the `.env` file to set your secret keys:

```env
HMAC_KEY=YourMcpHmacSecretKeyHere-AtLeast32Bytes!
JWT_KEY=YourSuperSecretKeyHere-AtLeast32Bytes!
JWT_ISS=HS-Agent
JWT_AUD=HS-Agent-Users
JWT_ACCESS_TOKEN_EXPIRATION_MINUTES=1
JWT_REFRESH_TOKEN_EXPIRATION_DAYS=30
RATE_LIMITING_PERMIT_LIMIT=0
RATE_LIMITING_WINDOW_SECONDS=0
RATE_LIMITING_QUEUE_LIMIT=0
```

> [!IMPORTANT]
> Never use example keys in production. Replace `HMAC_KEY` and `JWT_KEY` with unique, 32+ byte strings.

### 3. Launch the Application

```bash
docker compose up -d
```

### 4. Access the Service

- **Admin Panel:** `http://localhost:8080`
- **MCP Endpoint:** `http://localhost:8080/mcp`

## First-time Setup in Admin Panel

1. Open `http://localhost:8080` in your browser.
2. Create the first admin account and sign in.
3. Navigate to **DB Management** and add your database connection.
4. Go to **MCP Keys** and click **Issue Key**.
5. Associate the key with your database connection.
6. Copy the **Key Value** (only shown once) — you'll need it for client configuration.

## Client Configuration

Add the following to your MCP client configuration:

```json
{
  "mcpServers": {
    "hs-sql-agent": {
      "url": "http://localhost:8080/mcp",
      "headers": {
        "X-MCP-Server-Key": "<YOUR_MCP_KEY>"
      }
    }
  }
}
```

Works with **Claude Desktop**, **VS Code (Cline/Roo)**, and **Cursor**.

## Next Steps

- Learn about the [Architecture](Architecture)
- Explore all [Features](Features)
- Check the [Admin Panel Guide](Admin-Panel) for detailed administration
- See the [MCP Tools Reference](MCP-Tools-Reference) for available tools
