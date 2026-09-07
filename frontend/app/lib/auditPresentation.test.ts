import { describe, expect, it } from "vitest";
import {
  buildAuditExecutionSummary,
  formatAuditDefinition,
} from "./auditPresentation";

describe("audit presentation helpers", () => {
  it("pretty prints JSON definitions and preserves non-JSON text", () => {
    expect(formatAuditDefinition('{"sql":"select 1","args":{"id":7}}')).toBe(
      '{\n  "sql": "select 1",\n  "args": {\n    "id": 7\n  }\n}',
    );
    expect(formatAuditDefinition("SELECT 1")).toBe("SELECT 1");
    expect(formatAuditDefinition(null)).toBe("");
  });

  it("builds a compact execution summary without placeholder noise", () => {
    expect(
      buildAuditExecutionSummary({
        durationMs: 42,
        returnedRows: 8,
        affectedRows: null,
        approvalStatus: "Approved",
      }),
    ).toEqual(["42 ms", "8 returned", "approval Approved"]);

    expect(buildAuditExecutionSummary({})).toEqual([]);
  });
});
