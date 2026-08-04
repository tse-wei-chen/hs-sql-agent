import { xiorInstanceToken } from "./xiorInstance";

export interface IssueMcpKeyRequest {
  name: string;
  expiresAt?: string | null;
  allowedTools?: string | null;
  corsAllowedOrigins?: string | null;
  dbSettingMode?: 0 | 1;
  dbManagementId?: number | null;
  sqlProvider?: string | null;
  host?: string | null;
  port?: string | null;
  username?: string | null;
  password?: string | null;
  database?: string | null;
  extraSettings?: string | null;
  tableWhitelist?: string | null;
  rateLimitMode?: "Inherit" | "Custom" | "Unlimited";
  permitLimitOverride?: number | null;
  windowSecondsOverride?: number | null;
}

export interface TestDbConnectionRequest {
  dbSettingMode: 0 | 1;
  dbManagementId?: number | null;
  sqlProvider?: string | null;
  host?: string | null;
  port?: string | null;
  username?: string | null;
  password?: string | null;
  database?: string | null;
  extraSettings?: string | null;
}

export interface AuditDailySummaryItem {
  day: Date;
  success: number;
  failed: number;
}

export const listMcpKeys = async () => {
  const response = await xiorInstanceToken.get("/runtime/mcp-keys");
  return response.data;
};

export const issueMcpKey = async (payload: IssueMcpKeyRequest) => {
  const response = await xiorInstanceToken.post("/runtime/mcp-keys", payload);
  return response.data;
};

export const revokeMcpKey = async (id: number) => {
  const response = await xiorInstanceToken.post(
    `/runtime/mcp-keys/${id}/revoke`,
  );
  return response.data;
};

export const updateMcpKey = async (
  id: number,
  payload: IssueMcpKeyRequest,
) => {
  const response = await xiorInstanceToken.put(
    `/runtime/mcp-keys/${id}`,
    payload,
  );
  return response.data;
};

export const rotateMcpKey = async (
  id: number,
  payload: { gracePeriodMinutes: number; expiresAt?: string | null },
) => {
  const response = await xiorInstanceToken.post(
    `/runtime/mcp-keys/${id}/rotate`,
    payload,
  );
  return response.data;
};

export const cloneMcpKey = async (
  id: number,
  payload: { name: string; expiresAt?: string | null },
) => {
  const response = await xiorInstanceToken.post(
    `/runtime/mcp-keys/${id}/clone`,
    payload,
  );
  return response.data;
};

export const getRuntimeAudit = async (
  page = 1,
  pageSize = 20,
  filters: {
    action?: string;
    keyword?: string;
    from?: string;
    to?: string;
    result?: string;
    actor?: string;
    dbManagementId?: number;
    accessKeyId?: number;
    toolName?: string;
  } = {},
) => {
  const response = await xiorInstanceToken.get("/runtime/audit", {
    params: {
      page,
      pageSize,
      ...filters,
    },
  });
  return response.data;
};

export const getRuntimeAuditDailySummary = async (days = 7) => {
  const response = await xiorInstanceToken.get("/runtime/audit/daily-summary", {
    params: {
      days,
    },
  });
  return response.data;
};

export const exportRuntimeAudit = async (
  format: "csv" | "json",
  filters: Record<string, string | number | undefined>,
) => {
  const response = await xiorInstanceToken.get("/runtime/audit/export", {
    params: { format, ...filters },
    responseType: "blob",
  });
  return response.data as Blob;
};

export const getOperabilityMetrics = async (filters: Record<string, string | number | undefined> = {}) =>
  (await xiorInstanceToken.get("/runtime/operability/metrics", { params: filters })).data;

export const getDbHealth = async () =>
  (await xiorInstanceToken.get("/runtime/operability/db-health")).data;

export const getKeyUsage = async (filters: Record<string, string | number | undefined> = {}) =>
  (await xiorInstanceToken.get("/runtime/operability/key-usage", { params: filters })).data;

export const getDeliveryStatuses = async () =>
  (await xiorInstanceToken.get("/runtime/operability/deliveries")).data;

export const retryDelivery = async (id: number) =>
  xiorInstanceToken.post(`/runtime/operability/deliveries/${id}/retry`);

export const dryRunAuditRetention = async () =>
  (await xiorInstanceToken.post("/runtime/audit/retention/dry-run")).data;

export const executeAuditRetention = async () =>
  (await xiorInstanceToken.post("/runtime/audit/retention/execute")).data;

export const testDbConnection = async (payload: TestDbConnectionRequest) => {
  const response = await xiorInstanceToken.post(
    "/runtime/mcp-keys/test-db-connection",
    payload,
  );
  return response.data;
};
