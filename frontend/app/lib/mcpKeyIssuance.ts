export type McpKeyExpirationMode = null | 1 | 7 | 30 | "custom";

export interface McpKeyDetail {
  expiresAt: McpKeyExpirationMode;
  allowedTools: string[];
  corsAllowedOrigins: string;
  dbManagementId: number | null;
  tableWhitelist: string[];
}

const DEFAULT_ALLOWED_TOOLS = [
  "get_columns",
  "get_schemas",
  "get_tables",
  "execute_query_sql",
];

export function createInitialMcpKeyDetail(): McpKeyDetail {
  return {
    expiresAt: null,
    allowedTools: [...DEFAULT_ALLOWED_TOOLS],
    corsAllowedOrigins: "",
    dbManagementId: null,
    tableWhitelist: [],
  };
}

export function resolveMcpKeyExpiry(
  mode: McpKeyExpirationMode,
  customExpiresAt: string,
  now = new Date(),
): string | null {
  if (mode === null) {
    return null;
  }

  if (mode === "custom") {
    return customExpiresAt;
  }

  return new Date(now.getTime() + mode * 24 * 60 * 60 * 1000).toISOString();
}

export function serializeTableWhitelist(
  enabled: boolean,
  selectedTables: string[],
): string | null {
  if (!enabled) {
    return null;
  }

  if (selectedTables.length === 0) {
    throw new Error(
      "Select at least one table when data access restriction is enabled.",
    );
  }

  return selectedTables.join(",");
}

export function formatAllowedToolsLabel(selectedTools: string[]): string {
  return selectedTools.length === 0
    ? "Global (no restriction)"
    : `${selectedTools.length} tools selected`;
}
