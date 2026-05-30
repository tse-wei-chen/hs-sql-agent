# 📡 API Reference

## Overview

The REST API is used by the Admin Panel frontend. All API endpoints are prefixed with `/api` and require JWT authentication (except `sign-in` and `sign-up`).

## Authentication

### Sign In
```
POST /api/auth/sign-in
Content-Type: application/json

{
  "username": "admin",
  "password": "your-password"
}

Response: {
  "accessToken": "eyJ...",
  "refreshToken": "eyJ...",
  "expiresIn": 60
}
```

### Sign Up
```
POST /api/auth/sign-up
Content-Type: application/json

{
  "username": "admin",
  "password": "your-password"
}

Response: {
  "accessToken": "eyJ...",
  "refreshToken": "eyJ..."
}
```

### Refresh Token
```
POST /api/auth/refresh
Content-Type: application/json

{
  "refreshToken": "eyJ..."
}

Response: {
  "accessToken": "eyJ...",
  "refreshToken": "eyJ..."
}
```

## MCP Keys

### List Keys
```
GET /api/mcp-keys
Authorization: Bearer <token>

Response: [{
  "id": 1,
  "name": "My Key",
  "keyPrefix": "hs-...",
  "isActive": true,
  "lastUsedAt": "2026-05-22T10:00:00Z",
  "dbManagementId": 1,
  "dbManagementName": "My DB",
  "allowedTools": "execute_query_safe,get_tables",
  "tableWhitelist": ["users", "orders"],
  "createdAt": "2026-05-01T00:00:00Z"
}]
```

### Issue Key
```
POST /api/mcp-keys
Authorization: Bearer <token>
Content-Type: application/json

{
  "name": "My API Key",
  "dbManagementId": 1,
  "allowedTools": ["execute_query_safe", "get_tables", "get_columns"],
  "tableWhitelist": ["users", "orders"]
}

Response: {
  "id": 1,
  "keyValue": "hs-xxxxx...",  // shown only once!
  "name": "My API Key",
  ...
}
```

### Revoke Key
```
DELETE /api/mcp-keys/{id}
Authorization: Bearer <token>

Response: 204 No Content
```

## Database Management

### List Connections
```
GET /api/db-management
Authorization: Bearer <token>
```

### Add Connection
```
POST /api/db-management
Authorization: Bearer <token>
Content-Type: application/json

{
  "name": "Production DB",
  "provider": "PostgreSQL",
  "connectionString": "Host=...;Database=...;Username=...;Password=..."
}
```

### Update Connection
```
PUT /api/db-management/{id}
Authorization: Bearer <token>
Content-Type: application/json

{
  "name": "Production DB",
  "provider": "PostgreSQL",
  "connectionString": "Host=...;Database=...;Username=...;Password=..."
}
```

### Delete Connection
```
DELETE /api/db-management/{id}
Authorization: Bearer <token>
```

### Test Connection
```
POST /api/db-management/{id}/test
Authorization: Bearer <token>

Response: {
  "success": true,
  "message": "Connection successful",
  "elapsedMs": 123
}
```

## Semantic Layer

### Get Semantic Metadata
```
GET /api/semantic?dbManagementId=1
Authorization: Bearer <token>
```

### Update Semantic Metadata
```
PUT /api/semantic
Authorization: Bearer <token>
Content-Type: application/json

{
  "dbManagementId": 1,
  "tables": [
    {
      "name": "users",
      "displayName": "Users",
      "description": "User accounts table",
      "columns": [
        {
          "name": "id",
          "displayName": "User ID",
          "description": "Primary key"
        }
      ]
    }
  ]
}
```

## Custom Tools

### List Custom Tools
```
GET /api/custom-tools
Authorization: Bearer <token>
```

### Create Custom Tool
```
POST /api/custom-tools
Authorization: Bearer <token>
Content-Type: application/json

{
  "name": "calculate_churn_rate",
  "description": "Calculate churn rate for a given period",
  "type": "Query",
  "dbManagementId": 1,
  "parametersJson": "[{\"name\":\"startDate\",\"type\":\"string\",\"description\":\"Start date\"}]",
  "queryJson": "...",
  "dmlJson": null
}
```

### Update Custom Tool
```
PUT /api/custom-tools/{id}
Authorization: Bearer <token>
```

### Delete Custom Tool
```
DELETE /api/custom-tools/{id}
Authorization: Bearer <token>
```

## Audit Logs

### Get Audit Logs
```
GET /api/audit?page=1&pageSize=20&keyId=1&toolName=execute_query_safe&dateFrom=2026-05-01&dateTo=2026-05-22
Authorization: Bearer <token>
```

## Dashboard

### Get Dashboard Data
```
GET /api/dashboard
Authorization: Bearer <token>

Response: {
  "totalKeys": 5,
  "activeKeys": 3,
  "totalQueries": 1234,
  "recentAuditLogs": [...]
}
```

## MCP Endpoint

### MCP Protocol
```
POST /mcp
Content-Type: application/json
X-MCP-Server-Key: <mcp-key>

{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "execute_query_safe",
    "arguments": {
      "tableName": "users",
      "selectColumns": [{"field": "id"}, {"field": "name"}],
      "limit": 10
    }
  }
}
```

The MCP endpoint supports:
- `tools/list` — List available tools for this session
- `tools/call` — Invoke a specific tool

## Error Responses

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Invalid or expired token"
}
```

Common HTTP status codes:
| Status | Description |
| ------ | ----------- |
| 200    | Success     |
| 201    | Created     |
| 204    | No Content  |
| 400    | Bad Request |
| 401    | Unauthorized|
| 403    | Forbidden   |
| 404    | Not Found   |
| 429    | Rate Limit Exceeded |
| 500    | Internal Server Error |
