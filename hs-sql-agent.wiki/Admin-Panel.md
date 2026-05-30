# 🖥️ Admin Panel Guide

## Overview

The Admin Panel is a built-in web UI for managing hs-sql-agent. It allows you to monitor operations and manage access without touching configuration files.

Access at: `http://localhost:8080`

## Screenshots

### Operational Dashboard
![Dashboard](https://github.com/user-attachments/assets/4125fea0-ccad-4c56-aa75-bc9e396bec69)

Real-time monitoring of MCP keys and audit events.

### Key Management
![Key Management](https://github.com/user-attachments/assets/a4e25214-e9f4-4b8b-a1c1-d9e157e629dc)

Granular control: assign specific database connections and tool subsets to each API key.

### Custom SQL Tools
![Custom Tools](https://github.com/user-attachments/assets/78462e19-c7bf-4bfc-8475-7e8c91362eb2)

Low-code tools: define custom SQL operations for the AI agent.

### DB Management
![DB Management](https://github.com/user-attachments/assets/4d8e5876-61d8-4421-b6fc-ef42c18a3d2d)

Manage your database connections.

## Getting Started

### First-time Login

1. Open `http://localhost:8080` in your browser.
2. Click **Sign Up** to create the first admin account.
3. Sign in with your credentials.

## Sections

### Dashboard
The main landing page shows:
- Active MCP keys summary
- Recent audit log entries
- System status overview

### MCP Keys
Manage API keys for AI agent access:

- **Issue Key**: Create a new MCP API key
  - Assign to a database connection
  - Select allowed tools
  - Configure table whitelist
  - Copy the key value immediately (shown only once!)
- **List Keys**: View all active keys with metadata
- **Revoke Key**: Disable a key immediately

### DB Management
Manage database connections:

- **Add Connection**: Configure a new database
  - Select provider (SQLite, PostgreSQL, MySQL, SQL Server, Oracle, FireBird)
  - Enter connection string
  - Test connection before saving
- **Edit Connection**: Modify existing connection
- **Delete Connection**: Remove a connection
- **Semantic Layer**: Configure per-connection semantic metadata

### Custom SQL Tools
Create low-code tools for the AI agent:

1. Click **Create Tool**
2. Enter tool name and description
3. Define parameters (name, type, description)
4. Write the SQL query with `{{parameterName}}` placeholders
5. Choose operation type: Query (SELECT) or DML (INSERT/UPDATE/DELETE)
6. Test the tool in the UI
7. Save and it's automatically exposed as an MCP tool

### Audit Logs
View query execution history:

- Daily summaries of tool usage
- Detailed execution logs with parameters
- Filter by date, key, or tool
- Compliance-ready record keeping

### Semantic Layer
Define business metadata for databases:

- Set display names for tables and columns
- Add descriptions that help the AI agent understand data context
- Semantic data is merged into `get_tables` and `get_columns` responses

### Rate Limiting
Configure global rate limiting:

- **Permit Limit**: Max requests per time window
- **Window**: Time window in seconds
- **Queue Limit**: Max queued requests

## Navigation

The sidebar provides quick access to all sections:

```
🏠 Dashboard
🔑 MCP Keys
🗄️ DB Management
🔧 Custom Tools
📋 Audit Logs
📚 Semantic Layer
⚙️ Settings
```

## REST API Endpoints

The Admin Panel communicates with the backend via REST API:

| Endpoint              | Description                |
| --------------------- | -------------------------- |
| `POST /api/auth/sign-in` | Admin login              |
| `POST /api/auth/sign-up` | Admin registration       |
| `POST /api/auth/refresh` | Refresh JWT token        |
| `GET /api/mcp-keys`      | List MCP keys            |
| `POST /api/mcp-keys`     | Issue new MCP key        |
| `DELETE /api/mcp-keys/{id}` | Revoke MCP key        |
| `GET /api/db-management` | List DB connections       |
| `POST /api/db-management` | Add DB connection        |
| `PUT /api/db-management/{id}` | Update DB connection |
| `DELETE /api/db-management/{id}` | Delete DB connection |
| `POST /api/db-management/{id}/test` | Test connection |
| `GET /api/semantic`      | Get semantic metadata    |
| `PUT /api/semantic`      | Update semantic metadata |
| `GET /api/custom-tools`  | List custom tools        |
| `POST /api/custom-tools` | Create custom tool       |
| `PUT /api/custom-tools/{id}` | Update custom tool   |
| `DELETE /api/custom-tools/{id}` | Delete custom tool  |
| `GET /api/audit`         | Get audit logs           |
| `GET /api/dashboard`     | Get dashboard data       |
