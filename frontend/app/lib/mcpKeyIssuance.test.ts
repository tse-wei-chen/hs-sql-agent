import { describe, expect, it } from "vitest";
import {
  createInitialMcpKeyDetail,
  formatAllowedToolsLabel,
  resolveMcpKeyExpiry,
  serializeTableWhitelist,
  createMcpOnboardingSnippets,
  allowedToolsRequireElicitation,
  resolveDefaultAllowedTools,
  resolveMcpAccessPosture,
} from "./mcpKeyIssuance";

const catalog = [
  { name: "get_schemas", defaultSelected: true },
  { name: "get_tables", defaultSelected: true },
  { name: "get_columns", defaultSelected: true },
  { name: "execute_query_sql", defaultSelected: true },
  { name: "execute_dml_sql", defaultSelected: false },
] as const;

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

  it("derives four safe defaults from the server catalog and leaves DML opt-in", () => {
    const defaults = resolveDefaultAllowedTools(catalog);

    expect(defaults).toEqual([
      "get_schemas",
      "get_tables",
      "get_columns",
      "execute_query_sql",
    ]);
    expect(defaults).not.toContain("execute_dml_sql");
  });

  it("creates independent default array values for each form reset", () => {
    const defaults = resolveDefaultAllowedTools(catalog);
    const first = createInitialMcpKeyDetail(defaults);
    first.allowedTools.push("execute_dml_sql");
    first.tableWhitelist.push("public.users");

    const second = createInitialMcpKeyDetail(defaults);
    expect(second.allowedTools).toEqual(defaults);
    expect(second.allowedTools).not.toContain("execute_dml_sql");
    expect(second.tableWhitelist).toEqual([]);
    expect(second.allowedTools).not.toBe(first.allowedTools);
    expect(second.tableWhitelist).not.toBe(first.tableWhitelist);
  });

  it("classifies the default key posture as read/query only", () => {
    const posture = resolveMcpAccessPosture(
      resolveDefaultAllowedTools(catalog),
      ["execute_dml_sql"],
      false,
    );

    expect(posture).toEqual({
      level: "read-query",
      title: "Read/query only",
      description: "4 non-DML tools selected.",
      dataScope: "All tables",
    });
  });

  it("surfaces explicit DML and table-restricted posture", () => {
    const posture = resolveMcpAccessPosture(
      ["execute_query_sql", "execute_dml_sql"],
      ["execute_dml_sql"],
      true,
      2,
    );

    expect(posture.level).toBe("dml-enabled");
    expect(posture.title).toBe("DML enabled");
    expect(posture.description).toContain("human approval");
    expect(posture.dataScope).toBe("2 tables");
  });

  it("makes the empty-selection unrestricted posture explicit", () => {
    const posture = resolveMcpAccessPosture([], ["execute_dml_sql"], true, 1);

    expect(posture.level).toBe("unrestricted");
    expect(posture.title).toBe("Unrestricted tool access");
    expect(posture.description).toContain("including DML");
    expect(posture.dataScope).toBe("1 table");
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

  it("requires Elicitation for unrestricted or selected DML tools", () => {
    const dmlTools = ["execute_dml_sql", "archive_customer"];
    expect(allowedToolsRequireElicitation([], dmlTools)).toBe(true);
    expect(allowedToolsRequireElicitation(["execute_dml_sql"], dmlTools)).toBe(true);
    expect(allowedToolsRequireElicitation(["archive_customer"], dmlTools)).toBe(true);
    expect(allowedToolsRequireElicitation(["execute_query_sql"], dmlTools)).toBe(false);
  });
});