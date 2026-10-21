import { describe, expect, it } from "vitest";
import type { DmlReconciliationItem } from "@/api/dml-reconciliation";
import {
  buildDmlReconciliationRequest,
  canSubmitDmlReconciliation,
  dmlCommitEvidenceDescription,
  hasExplicitTimezone,
  permittedDmlResolutions,
  type DmlReconciliationForm,
} from "./dmlReconciliation";

const base: DmlReconciliationItem = {
  requestId: "approval-001",
  status: "OutcomeUnknown",
  updatedAt: "2026-10-21T09:22:30.123Z",
  createdAt: "2026-10-21T09:20:00.123Z",
  dbManagementId: 2,
  databaseProvider: "Sqlite",
  databaseIdentity: "inventory",
  statementCount: 2,
  commitEvidence: "PhysicalCommitUnknown",
  allowedResolutions: ["committed", "not_committed", "indeterminate"],
};

const form: DmlReconciliationForm = {
  resolution: "indeterminate",
  evidenceReference: "INC-123",
  note: "The operator checked an independent database log.",
  confirmation: base.requestId,
  confirmedIndependentReview: true,
};

describe("durable DML reconciliation operator guardrails", () => {
  it("never offers a resolution to an Executing or unknown state", () => {
    expect(permittedDmlResolutions({
      ...base, status: "Executing", commitEvidence: "WorkerFencingRequired",
      allowedResolutions: ["committed"],
    })).toEqual([]);
    expect(permittedDmlResolutions({ ...base, status: "FutureState" })).toEqual([]);
    expect(permittedDmlResolutions({ ...base, commitEvidence: "Unrecognized" })).toEqual([]);
    expect(dmlCommitEvidenceDescription("WorkerFencingRequired")).toContain("No settlement");
  });

  it("preserves confirmed COMMIT evidence for legacy and new uncertainty states", () => {
    for (const status of ["OutcomeUnknown", "ApprovalStateUnknown"]) {
      const item = {
        ...base, status, commitEvidence: "TargetCommitAcknowledged",
        allowedResolutions: ["committed", "not_committed", "indeterminate"] as DmlReconciliationItem["allowedResolutions"],
      };
      expect(permittedDmlResolutions(item)).toEqual(["committed"]);
      expect(canSubmitDmlReconciliation(item, form)).toBe(false);
      expect(canSubmitDmlReconciliation(item, { ...form, resolution: "committed" })).toBe(true);
    }
  });

  it("exposes rollback uncertainty separately from a lost COMMIT acknowledgement", () => {
    const item: DmlReconciliationItem = {
      ...base, status: "RollbackOutcomeUnknown",
      commitEvidence: "RollbackUnconfirmed",
    };
    expect(permittedDmlResolutions(item)).toEqual([
      "committed", "not_committed", "indeterminate",
    ]);
    expect(canSubmitDmlReconciliation(item, form)).toBe(true);
    expect(dmlCommitEvidenceDescription(item.commitEvidence)).toContain("could not confirm rollback");
    expect(permittedDmlResolutions({ ...item, status: "Executing" })).toEqual([]);
  });

  it("requires independent review, bounded evidence and exact request ID", () => {
    expect(canSubmitDmlReconciliation(base, form)).toBe(true);
    expect(canSubmitDmlReconciliation(base, { ...form, confirmedIndependentReview: false })).toBe(false);
    expect(canSubmitDmlReconciliation(base, { ...form, confirmation: "approval-00" })).toBe(false);
    expect(canSubmitDmlReconciliation(base, { ...form, evidenceReference: " " })).toBe(false);
    expect(canSubmitDmlReconciliation(base, { ...form, evidenceReference: "x".repeat(257) })).toBe(false);
    expect(canSubmitDmlReconciliation(base, { ...form, note: "x".repeat(701) })).toBe(false);
    expect(canSubmitDmlReconciliation(base, { ...form, resolution: "" })).toBe(false);
    expect(canSubmitDmlReconciliation(base, { ...form, resolution: "not_committed" })).toBe(true);
  });

  it("sends the exact inspected status and timezone-aware timestamp without recomputing it", () => {
    expect(buildDmlReconciliationRequest(base, {
      ...form, evidenceReference: " INC-123 ", note: " Reviewed logs ",
    })).toEqual({
      expectedStatus: "OutcomeUnknown",
      expectedUpdatedAt: base.updatedAt,
      resolution: "indeterminate",
      evidenceReference: "INC-123",
      note: "Reviewed logs",
    });
    expect(hasExplicitTimezone("2026-10-21T09:22:30Z")).toBe(true);
    expect(hasExplicitTimezone("2026-10-21T17:22:30+08:00")).toBe(true);
    expect(hasExplicitTimezone("2026-10-21T09:22:30")).toBe(false);
    expect(canSubmitDmlReconciliation(
      { ...base, updatedAt: "2026-10-21T09:22:30" }, form,
    )).toBe(false);
    expect(() => buildDmlReconciliationRequest(base,
      { ...form, confirmation: "wrong" })).toThrow("explicit confirmation");
  });

  it("never enables an action absent from the server allowlist", () => {
    expect(permittedDmlResolutions({
      ...base, allowedResolutions: ["not_committed"],
    })).toEqual(["not_committed"]);
    expect(permittedDmlResolutions({
      ...base, allowedResolutions: [],
    })).toEqual([]);
    expect(permittedDmlResolutions({
      ...base, status: "ApprovalStateUnknown", commitEvidence: "PhysicalCommitUnknown",
    })).toEqual([]);
  });
});
