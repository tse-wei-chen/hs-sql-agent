import { describe, expect, it } from "vitest";
import {
  createInitialMcpKeyDetail,
  formatAllowedToolsLabel,
  resolveMcpKeyExpiry,
  serializeTableWhitelist,
  createMcpOnboardingSnippets,
  getMcpEndpoint,
  runMcpSmokeTest,
  allowedToolsRequireElicitation,
} from "./mcpKeyIssuance";

describe("MCP key issuance helpers", () => {
  it.each([
    [1, "2026-07-31T12:00:00.000Z"],
    [7, "2026-08-06T12:00:00.000Z"],
    [30, "2026-08-29T12:00:00.000Z"],
  ] as const)("converts a %i-day expiry into a future timestamp", (days, expected) => {
    const now = new Date("2026-07-30T12:00:00.000Z");

    expect(resolveMcpKeyExpiry(days, "", now)).toBe(expected);
  });

  it("preserves never mode and converts a local custom time to UTC", () => {
    expect(resolveMcpKeyExpiry(null, "")).toBeNull();
    const localDateTime = "2026-08-01T09:30";
    expect(resolveMcpKeyExpiry("custom", localDateTime)).toBe(
      new Date(localDateTime).toISOString(),
    );
  });

  it("rejects missing or invalid custom expiry values", () => {
    expect(() => resolveMcpKeyExpiry("custom", "")).toThrow(
      "Select a custom expiration",
    );
    expect(() => resolveMcpKeyExpiry("custom", "invalid")).toThrow(
      "Enter a valid custom expiration",
    );
  });

  it("rejects an enabled whitelist with no selected tables", () => {
    expect(() => serializeTableWhitelist(true, [])).toThrow(
      "Select at least one table",
    );
  });

  it("keeps a disabled whitelist unrestricted and serializes selected tables", () => {
    expect(serializeTableWhitelist(false, [])).toBeNull();
    expect(
      serializeTableWhitelist(true, ["public.users", "sales.orders"]),
    ).toBe("public.users,sales.orders");
  });

  it("describes the actual allowed-tools selection", () => {
    expect(formatAllowedToolsLabel([])).toBe("Global (no restriction)");
    expect(formatAllowedToolsLabel(["get_tables", "execute_query_sql"])).toBe(
      "2 tools selected",
    );
  });

  it("creates independent default array values for each form reset", () => {
    const first = createInitialMcpKeyDetail();
    first.allowedTools.push("execute_dml_sql");
    first.tableWhitelist.push("public.users");

    const second = createInitialMcpKeyDetail();
    expect(second.allowedTools).not.toContain("execute_dml_sql");
    expect(second.tableWhitelist).toEqual([]);
    expect(second.allowedTools).not.toBe(first.allowedTools);
    expect(second.tableWhitelist).not.toBe(first.tableWhitelist);
  });

  it("builds direct Streamable HTTP snippets with the MCP server key header", () => {
    const snippets = createMcpOnboardingSnippets("https://sql.example.com/mcp", "secret-key");
    expect(JSON.parse(snippets.cursor).mcpServers["hs-sql-agent"]).toEqual({
      type: "http",
      url: "https://sql.example.com/mcp",
      headers: { "X-MCP-Server-Key": "secret-key" },
    });
    expect(JSON.parse(snippets.claudeDesktop).mcpServers["hs-sql-agent"]).toEqual({
      url: "https://sql.example.com/mcp",
      headers: { "X-MCP-Server-Key": "secret-key" },
    });
    expect(JSON.parse(snippets.genericHttp)).toEqual({
      type: "streamable-http",
      url: "https://sql.example.com/mcp",
      headers: { "X-MCP-Server-Key": "secret-key" },
    });
    expect(getMcpEndpoint("https://sql.example.com/")).toBe("https://sql.example.com/mcp");
  });

  it("requires Elicitation for unrestricted, built-in DML, and custom DML access", () => {
    expect(allowedToolsRequireElicitation([])).toBe(true);
    expect(allowedToolsRequireElicitation(["execute_dml_sql"])).toBe(true);
    expect(allowedToolsRequireElicitation(["archive_customer"], ["archive_customer"])).toBe(true);
    expect(allowedToolsRequireElicitation(["execute_query_sql"], ["archive_customer"])).toBe(false);
  });

  it("separates auth rejection from network and capability results", async () => {
    const request = async () => new Response("unauthorized", { status: 401 });
    const result = await runMcpSmokeTest("https://sql.example.com/mcp", "bad", request as typeof fetch);
    expect(result.network.status).toBe("passed");
    expect(result.auth.status).toBe("failed");
    expect(result.capability.status).toBe("not-run");
  });

  it("reports MCP tools capability after successful initialization", async () => {
    const request = async () => new Response(JSON.stringify({
      jsonrpc: "2.0",
      id: 1,
      result: {
        protocolVersion: "2025-11-25",
        capabilities: { tools: {} },
        serverInfo: { name: "HsSqlAgent", version: "1.0.0" },
      },
    }), { status: 200 });
    const result = await runMcpSmokeTest("https://sql.example.com/mcp", "key", request as typeof fetch);
    expect(result.network.status).toBe("passed");
    expect(result.auth.status).toBe("passed");
    expect(result.capability.status).toBe("passed");
    expect(result.protocolVersion).toBe("2025-11-25");
  });

  it("reports network failures without claiming authentication was tested", async () => {
    const request = async () => { throw new TypeError("Failed to fetch"); };
    const result = await runMcpSmokeTest("https://offline.example/mcp", "key", request as typeof fetch);
    expect(result.network.status).toBe("failed");
    expect(result.auth.status).toBe("not-run");
    expect(result.capability.status).toBe("not-run");
  });

  it("uses X-MCP-Server-Key for the one-time smoke test", async () => {
    let requestHeaders: HeadersInit | undefined;
    const request = async (_input: RequestInfo | URL, init?: RequestInit) => {
      requestHeaders = init?.headers;
      return new Response(JSON.stringify({
        result: {
          protocolVersion: "2025-11-25",
          capabilities: { tools: {} },
        },
      }), { status: 200 });
    };

    await runMcpSmokeTest("https://sql.example.com/mcp", "secret-key", request as typeof fetch);

    expect(requestHeaders).toMatchObject({ "X-MCP-Server-Key": "secret-key" });
    expect(requestHeaders).not.toHaveProperty("Authorization");
  });
});
