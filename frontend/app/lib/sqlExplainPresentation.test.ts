import { describe, expect, it } from "vitest";
import type { SqlExplainContext, SqlExplainResult } from "@/api/security";
import {
  GLOBAL_POLICY_LABEL,
  accessKeyOptions,
  databaseLabel,
  explainOutcome,
  formatParameterValue,
  resolveAccessKey,
  resolveDatabase,
} from "./sqlExplainPresentation";

const context: SqlExplainContext = {
  databases: [
    { id: 1, name: "Sales", sqlProvider: "Postgres" },
    { id: 2, name: "Warehouse", sqlProvider: "MySQL" },
  ],
  accessKeys: [
    {
      id: 10,
      name: "sales-read",
      dbManagementId: 1,
      isActive: true,
      isExpired: false,
    },
    {
      id: 11,
      name: "warehouse-old",
      dbManagementId: 2,
      isActive: false,
      isExpired: true,
    },
  ],
};

describe("sql explain presentation", () => {
  it("keeps database and MCP key selection scoped to the chosen database", () => {
    const salesLabel = databaseLabel(context.databases[0]!);
    expect(resolveDatabase(context, salesLabel)?.id).toBe(1);

    const options = accessKeyOptions(context, 1);
    expect(options[0]).toBe(GLOBAL_POLICY_LABEL);
    expect(options).toContain("sales-read · #10");
    expect(options.some(option => option.includes("warehouse-old"))).toBe(false);
    expect(resolveAccessKey(context, "sales-read · #10")?.id).toBe(10);
    expect(resolveAccessKey(context, GLOBAL_POLICY_LABEL)).toBeUndefined();
  });

  it("distinguishes translated, compiler rejected, and authorization denied outcomes", () => {
    const base = {
      simulationMode: "compile-only",
      executed: false,
      statementType: "Query",
      sourceDialect: "Postgres",
      targetProvider: "Postgres",
      scope: {
        keyActive: true,
        requiredTool: "execute_query_sql",
        toolAllowed: true,
        toolScope: "Global policy only",
        tableScope: "All tables",
        allowedTables: [],
      },
      policy: {
        queryMaxRows: 100,
        queryTimeoutSeconds: 30,
        requireWhereForUpdate: true,
        requireWhereForDelete: true,
        allowFullTableUpdate: false,
        allowFullTableDelete: false,
        dmlMaxAffectedRows: 100,
        dmlAffectedRowLimitEvaluated: false,
      },
      statements: [],
    } satisfies Omit<SqlExplainResult, "success">;

    expect(explainOutcome({ ...base, success: true }).label).toBe("Translated");
    expect(
      explainOutcome({
        ...base,
        success: false,
        failure: {
          code: "SQL_POLICY_DENIED",
          message: "denied",
          stage: "Policy",
          retryable: false,
          diagnostics: [],
        },
      }).label,
    ).toBe("Rejected");
    expect(
      explainOutcome({
        ...base,
        success: false,
        failure: {
          code: "authorization.tool_denied",
          message: "denied",
          stage: "Authorization",
          retryable: false,
          diagnostics: [],
        },
      }).label,
    ).toBe("Denied");
  });

  it("formats parameter values without flattening their types", () => {
    expect(formatParameterValue("active")).toBe('"active"');
    expect(formatParameterValue(42)).toBe("42");
    expect(formatParameterValue(null)).toBe("null");
  });
});
