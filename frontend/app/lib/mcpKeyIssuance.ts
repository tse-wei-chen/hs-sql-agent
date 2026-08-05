export type McpKeyExpirationMode = null | 1 | 7 | 30 | "custom";
export type McpKeyRateLimitMode = "Inherit" | "Custom" | "Unlimited";

export interface McpKeyDetail {
  expiresAt: McpKeyExpirationMode;
  allowedTools: string[];
  corsAllowedOrigins: string;
  dbManagementId: number | null;
  tableWhitelist: string[];
  rateLimitMode: McpKeyRateLimitMode;
  permitLimitOverride: number;
  windowSecondsOverride: number;
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
    rateLimitMode: "Inherit",
    permitLimitOverride: 120,
    windowSecondsOverride: 60,
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
    if (!customExpiresAt) {
      throw new Error("Select a custom expiration date and time.");
    }

    const parsed = new Date(customExpiresAt);
    if (Number.isNaN(parsed.getTime())) {
      throw new Error("Enter a valid custom expiration date and time.");
    }

    return parsed.toISOString();
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

export function allowedToolsRequireElicitation(
  allowedTools: string[],
  customDmlToolNames: Iterable<string> = [],
): boolean {
  const customDml = new Set(customDmlToolNames);
  return allowedTools.length === 0 ||
    allowedTools.includes("execute_dml_sql") ||
    allowedTools.some((name) => customDml.has(name));
}

export interface McpOnboardingSnippets {
  claudeDesktop: string;
  cursor: string;
  genericHttp: string;
}

export function createMcpOnboardingSnippets(
  endpoint: string,
  plaintextKey: string,
): McpOnboardingSnippets {
  const headers = { "X-MCP-Server-Key": plaintextKey };
  const server = {
    type: "http",
    url: endpoint,
    headers,
  };
  const genericServer = { ...server, type: "streamable-http" };
  const claudeDesktopServer = { url: endpoint, headers };

  return {
    claudeDesktop: JSON.stringify({ mcpServers: { "hs-sql-agent": claudeDesktopServer } }, null, 2),
    cursor: JSON.stringify({ mcpServers: { "hs-sql-agent": server } }, null, 2),
    genericHttp: JSON.stringify(genericServer, null, 2),
  };
}
