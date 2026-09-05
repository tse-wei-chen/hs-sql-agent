import { describe, expect, it } from "vitest";
import {
  createInitialMcpKeyDetail,
  formatAllowedToolsLabel,
  resolveMcpKeyExpiry,
  serializeTableWhitelist,
  createMcpOnboardingSnippets,
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
    expect(JSON.parse(snippets.vscode).servers["hs-sql-agent"]).toEqual({
      type: "http",
      url: "https://sql.example.com/mcp",
      headers: { "X-MCP-Server-Key": "secret-key" },
    });
    expect(JSON.parse(snippets.genericHttp)).toEqual({
      type: "streamable-http",
      url: "https://sql.example.com/mcp",
      headers: { "X-MCP-Server-Key": "secret-key" },
    });
  });

  it("requires Elicitation for unrestricted, built-in DML, and custom DML access", () => {
    expect(allowedToolsRequireElicitation([])).toBe(true);
    expect(allowedToolsRequireElicitation(["execute_dml_sql"])).toBe(true);
    expect(allowedToolsRequireElicitation(["archive_customer"], ["archive_customer"])).toBe(true);
    expect(allowedToolsRequireElicitation(["execute_query_sql"], ["archive_customer"])).toBe(false);
  });

});
