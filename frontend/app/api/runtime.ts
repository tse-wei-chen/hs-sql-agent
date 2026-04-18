import { xiorInstanceToken } from './xiorInstance'

export interface IssueMcpKeyRequest {
  name: string
  expiresAt?: string | null
  allowedTools?: string | null
  corsAllowedOrigins?: string | null
  sqlProvider?: string | null
  host?: string | null
  port?: string | null
  username?: string | null
  password?: string | null
  database?: string | null
}

export interface AuditDailySummaryItem {
  day: Date
  success: number
  failed: number
}

export const listMcpKeys = async () => {
  const response = await xiorInstanceToken.get('/runtime/mcp-keys')
  return response.data
}

export const issueMcpKey = async (payload: IssueMcpKeyRequest) => {
  const response = await xiorInstanceToken.post('/runtime/mcp-keys', payload)
  return response.data
}

export const revokeMcpKey = async (id: number) => {
  const response = await xiorInstanceToken.post(`/runtime/mcp-keys/${id}/revoke`)
  return response.data
}

export const getRuntimeAudit = async (page = 1, pageSize = 20, action = '', keyword = '') => {
  const response = await xiorInstanceToken.get('/runtime/audit', {
    params: {
      page,
      pageSize,
      action: action || undefined,
      keyword: keyword || undefined,
    },
  })
  return response.data
}

export const getRuntimeAuditDailySummary = async (days = 7) => {
  const response = await xiorInstanceToken.get('/runtime/audit/daily-summary', {
    params: {
      days,
    },
  })
  return response.data
}

export const testDbConnection = async (sqlProvider: string, host: string, port: string, username: string, password: string, database: string) => {
  const response = await xiorInstanceToken.post('/runtime/mcp-keys/test-db-connection', {
    sqlProvider: sqlProvider,
    host: host,
    port: port,
    username: username,
    password: password,
    database: database,
  })
  return response.data
}