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

export interface McpToolSelectionDescriptor {
  name: string;
  defaultSelected: boolean;
}

export type McpAccessPostureLevel =
  | "read-query"
  | "dml-enabled"
  | "unrestricted";

export interface McpAccessPosture {
  level: McpAccessPostureLevel;
  title: string;
  description: string;
  dataScope: string;
}

export function resolveDefaultAllowedTools(
  tools: readonly McpToolSelectionDescriptor[],
): string[] {
  return tools.filter((tool) => tool.defaultSelected).map((tool) => tool.name);
}

export function createInitialMcpKeyDetail(
  defaultAllowedTools: Iterable<string> = [],
): McpKeyDetail {
  return {
    expiresAt: null,
    allowedTools: [...defaultAllowedTools],
    corsAllowedOrigins: "",
    dbManagementId: null,
    tableWhitelist: [],
    rateLimitMode: "Inherit",
    permitLimitOverride: 120,
    windowSecondsOverride: 60,
  };
}

export function resolveMcpAccessPosture(
  allowedTools: readonly string[],
  dmlToolNames: Iterable<string> = [],
  restrictTables = false,
  selectedTableCount = 0,
): McpAccessPosture {
  const dataScope = restrictTables
    ? `${selectedTableCount} table${selectedTableCount === 1 ? "" : "s"}`
    : "All tables";

  if (allowedTools.length === 0) {
    return {
      level: "unrestricted",
      title: "Unrestricted tool access",
      description:
        "All built-in and published tools for this database are allowed, including DML.",
      dataScope,
    };
  }

  const dmlTools = new Set(dmlToolNames);
  const dmlEnabled = allowedTools.some((name) => dmlTools.has(name));

  if (dmlEnabled) {
    return {
      level: "dml-enabled",
      title: "DML enabled",
      description: `${allowedTools.length} tools selected. DML still requires the configured human approval flow.`,
      dataScope,
    };
  }

  return {
    level: "read-query",
    title: "Read/query only",
    description: `${allowedTools.length} non-DML tools selected.`,
    dataScope,
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
  dmlToolNames: Iterable<string> = [],
): boolean {
  if (allowedTools.length === 0) return true;
  const dmlTools = new Set(dmlToolNames);
  return allowedTools.some((name) => dmlTools.has(name));
}

export interface McpOnboardingSnippets {
  claudeDesktop: string;
  cursor: string;
  vscode: string;
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
    vscode: JSON.stringify({ servers: { "hs-sql-agent": server } }, null, 2),
    genericHttp: JSON.stringify(genericServer, null, 2),
  };
}