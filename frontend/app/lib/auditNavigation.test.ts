import { describe, expect, it } from "vitest";
import {
  buildAuditDrillDownQuery,
  parseAuditDrillDownQuery,
} from "./auditNavigation";

describe("audit drill-down query helpers", () => {
  it("serializes operability filters into stable audit query parameters", () => {
    expect(
      buildAuditDrillDownQuery({
        from: "2026-09-01",
        to: "2026-09-07",
        dbManagementId: 12,
        accessKeyId: 7,
        toolName: " execute_query_sql ",
      }),
    ).toEqual({
      from: "2026-09-01",
      to: "2026-09-07",
      dbManagementId: "12",
      accessKeyId: "7",
      toolName: "execute_query_sql",
    });
  });

  it("parses only valid date and positive numeric drill-down filters", () => {
    expect(
      parseAuditDrillDownQuery({
        from: "2026-09-01",
        to: ["2026-09-07", "ignored"],
        dbManagementId: "12",
        accessKeyId: "7",
        toolName: " execute_dml_sql ",
      }),
    ).toEqual({
      from: "2026-09-01",
      to: "2026-09-07",
      dbManagementId: 12,
      accessKeyId: 7,
      toolName: "execute_dml_sql",
    });
  });

  it("ignores malformed or non-positive URL values", () => {
    expect(
      parseAuditDrillDownQuery({
        from: "yesterday",
        to: "2026-9-7",
        dbManagementId: "0",
        accessKeyId: "7.5",
        toolName: "  ",
      }),
    ).toEqual({
      from: undefined,
      to: undefined,
      dbManagementId: undefined,
      accessKeyId: undefined,
      toolName: undefined,
    });
  });
});
