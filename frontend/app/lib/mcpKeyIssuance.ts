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
  | "ddl-enabled"
  | "dml-ddl-enabled"
  | "unrestricted"
  | "review";

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
  unclassifiedToolCount = 0,
  legacyUnrestricted = false,
  ddlToolNames: Iterable<string> = [],
): McpAccessPosture {
  const dataScope = restrictTables
    ? `${selectedTableCount} table${selectedTableCount === 1 ? "" : "s"}`
    : "All tables";

  if (allowedTools.length === 0) {
    return legacyUnrestricted
      ? {
          level: "unrestricted",
          title: "Legacy unrestricted tool access",
          description:
            "Existing unrestricted key: general built-in and published tools, including DML, are allowed. DDL requires an explicit execute_ddl_sql grant and separate approval; a blank allowlist never grants DDL.",
          dataScope,
        }
      : {
          level: "review",
          title: "Select allowed tools",
          description:
            "Select at least one tool. Empty selections cannot create unrestricted MCP keys.",
          dataScope,
        };
  }

  if (unclassifiedToolCount > 0) {
    return {
      level: "review",
      title: "Review tool scope",
      description: `${allowedTools.length} tools are saved, but ${unclassifiedToolCount} ${unclassifiedToolCount === 1 ? "tool is" : "tools are"} not in the current published catalog.`,
      dataScope,
    };
  }

  const dmlTools = new Set(
    Array.from(dmlToolNames, (name) => name.toLocaleLowerCase()),
  );
  const dmlEnabled = allowedTools.some((name) =>
    dmlTools.has(name.toLocaleLowerCase()),
  );

  const ddlTools = new Set(
    Array.from(ddlToolNames, (name) => name.toLocaleLowerCase()),
  );
  const ddlEnabled = allowedTools.some((name) =>
    ddlTools.has(name.toLocaleLowerCase()),
  );

  if (ddlEnabled && dmlEnabled) {
    return {
      level: "dml-ddl-enabled",
      title: "DML & DDL enabled",
      description: `${allowedTools.length} tools selected. DML commits and DDL execution have separate approval flows. DDL additionally requires Host enablement and live schema verification.`,
      dataScope,
    };
  }

  if (ddlEnabled) {
    return {
      level: "ddl-enabled",
      title: "DDL enabled",
      description: `${allowedTools.length} tools selected. DDL execution requires separate DDL approval, Host enablement, and live schema verification; a key grant alone does not authorize changes.`,
      dataScope,
    };
  }

  if (dmlEnabled) {
    return {
      level: "dml-enabled",
      title: "DML enabled",
      description: `${allowedTools.length} tools selected. DML commits still require the configured approval flow.`,
      dataScope,
    };
  }

  return {
    level: "read-query",
    title: "Read/query only",
    description: `${allowedTools.length} read/query tools selected.`,
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

export function formatAllowedToolsLabel(
  selectedTools: string[],
  legacyUnrestricted = false,
): string {
  return selectedTools.length === 0
    ? legacyUnrestricted
      ? "Legacy unrestricted (existing key)"
      : "Select at least one tool"
    : `${selectedTools.length} tools selected`;
}

export function allowedToolsRequireElicitation(
  allowedTools: string[],
  dmlToolNames: Iterable<string> = [],
  legacyUnrestricted = false,
): boolean {
  if (allowedTools.length === 0) return legacyUnrestricted;
  const dmlTools = new Set(
    Array.from(dmlToolNames, (name) => name.toLocaleLowerCase()),
  );
  return allowedTools.some((name) =>
    dmlTools.has(name.toLocaleLowerCase()),
  );
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