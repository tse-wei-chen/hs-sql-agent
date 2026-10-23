import { describe, expect, it } from "vitest";
import {
  buildAuditExecutionSummary,
  formatAuditDefinition,
  isAuditCommitOutcomeUnknown,
  auditOutcomeLabel,
} from "./auditPresentation";

describe("audit presentation helpers", () => {
  it("pretty prints JSON definitions and preserves non-JSON text", () => {
    expect(formatAuditDefinition('{"sql":"select 1","args":{"id":7}}')).toBe(
      '{\n  "sql": "select 1",\n  "args": {\n    "id": 7\n  }\n}',
    );
    expect(formatAuditDefinition("SELECT 1")).toBe("SELECT 1");
    expect(formatAuditDefinition(null)).toBe("");
  });

  it("separates uncertain DML commits from ordinary failed audits", () => {
    expect(isAuditCommitOutcomeUnknown({ result: "outcome_unknown" })).toBe(true);
    expect(isAuditCommitOutcomeUnknown({
      result: "failed",
      errorCategory: "DmlCommitOutcomeUnknownException",
    })).toBe(true);
    expect(isAuditCommitOutcomeUnknown({ result: "failed" })).toBe(false);
    expect(auditOutcomeLabel({ result: "outcome_unknown" })).toBe(
      "Commit outcome unknown",
    );
    expect(auditOutcomeLabel({ result: "success" })).toBe("success");
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
