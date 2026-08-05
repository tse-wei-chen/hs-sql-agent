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

export interface McpSmokeStage {
  status: "passed" | "failed" | "not-run";
  message: string;
}

export interface McpSmokeResult {
  network: McpSmokeStage;
  auth: McpSmokeStage;
  capability: McpSmokeStage;
  protocolVersion?: string;
  serverName?: string;
}

export function getMcpEndpoint(origin: string): string {
  return `${origin.replace(/\/$/, "")}/mcp`;
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

interface McpInitializeResponse {
  result?: {
    protocolVersion?: string;
    capabilities?: { tools?: object };
    serverInfo?: { name?: string };
  };
  error?: { message?: string };
}

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

function parseMcpResponse(body: string): McpInitializeResponse | null {
  const trimmed = body.trim();
  if (!trimmed) return null;
  if (trimmed.startsWith("{")) return JSON.parse(trimmed);

  const dataLine = trimmed
    .split(/\r?\n/)
    .find((line) => line.startsWith("data:"));
  return dataLine ? JSON.parse(dataLine.slice(5).trim()) : null;
}

export async function runMcpSmokeTest(
  endpoint: string,
  plaintextKey: string,
  request: typeof fetch = fetch,
): Promise<McpSmokeResult> {
  const result: McpSmokeResult = {
    network: { status: "not-run", message: "No response received." },
    auth: { status: "not-run", message: "Authentication was not checked." },
    capability: { status: "not-run", message: "MCP initialization was not checked." },
  };

  let response: Response;
  try {
    response = await request(endpoint, {
      method: "POST",
      headers: {
        "X-MCP-Server-Key": plaintextKey,
        Accept: "application/json, text/event-stream",
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 1,
        method: "initialize",
        params: {
          protocolVersion: "2025-11-25",
          capabilities: { elicitation: { form: {} } },
          clientInfo: { name: "hs-sql-agent-admin-smoke", version: "1.0.0" },
        },
      }),
    });
    result.network = { status: "passed", message: `HTTP ${response.status} received.` };
  } catch (error: unknown) {
    result.network = {
      status: "failed",
      message: errorMessage(error, "The MCP endpoint could not be reached."),
    };
    return result;
  }

  if (response.status === 401 || response.status === 403) {
    result.auth = { status: "failed", message: `Key rejected with HTTP ${response.status}.` };
    return result;
  }
  if (!response.ok) {
    result.auth = { status: "not-run", message: `Authentication is inconclusive (HTTP ${response.status}).` };
    result.capability = { status: "failed", message: "Endpoint did not return a successful MCP response." };
    return result;
  }
  result.auth = { status: "passed", message: "The one-time key was accepted." };

  try {
    const payload = parseMcpResponse(await response.text());
    const initialized = payload?.result;
    if (!initialized?.protocolVersion || !initialized?.capabilities?.tools) {
      result.capability = {
        status: "failed",
        message: payload?.error?.message || "Response is not an MCP initialize result with tools capability.",
      };
      return result;
    }
    result.protocolVersion = initialized.protocolVersion;
    result.serverName = initialized.serverInfo?.name;
    result.capability = {
      status: "passed",
      message: `MCP ${initialized.protocolVersion} initialized and tools capability is available.`,
    };
  } catch (error: unknown) {
    result.capability = {
      status: "failed",
      message: errorMessage(error, "MCP response could not be parsed."),
    };
  }
  return result;
}
