import { describe, expect, it } from "vitest";
import type { SecurityPolicy } from "@/api/security";
import {
  buildSecurityPolicyPosture,
  securityPolicyFingerprint,
} from "./securityPolicyPresentation";

const policy = (overrides: Partial<SecurityPolicy> = {}): SecurityPolicy => ({
  queryMaxRows: 1000,
  queryTimeoutSeconds: 30,
  requireWhereForUpdate: true,
  requireWhereForDelete: true,
  allowFullTableUpdate: false,
  allowFullTableDelete: false,
  dmlMaxAffectedRows: 100,
  keyPermitLimit: 120,
  keyWindowSeconds: 60,
  maxConcurrentSql: 16,
  ...overrides,
});

describe("buildSecurityPolicyPosture", () => {
  it("reports a guarded mutation policy when WHERE is required and full-table mutation is blocked", () => {
    const result = buildSecurityPolicyPosture(policy());

    expect(result.label).toBe("Guarded mutation policy");
    expect(result.requiresReview).toBe(false);
    expect(result.warnings).toEqual([]);
    expect(result.facts).toContainEqual({
      label: "Full-table mutation",
      value: "UPDATE blocked · DELETE blocked",
    });
  });

  it("surfaces every relaxed mutation guardrail without inventing a security score", () => {
    const result = buildSecurityPolicyPosture(
      policy({
        requireWhereForUpdate: false,
        allowFullTableUpdate: true,
        allowFullTableDelete: true,
      }),
    );

    expect(result.label).toBe("Review mutation policy");
    expect(result.requiresReview).toBe(true);
    expect(result.warnings).toEqual([
      "UPDATE can run without a WHERE clause.",
      "Full-table UPDATE is allowed by the current policy.",
      "Full-table DELETE is allowed by the current policy.",
    ]);
  });
});

describe("securityPolicyFingerprint", () => {
  it("ignores server audit metadata but changes when an enforced value changes", () => {
    const original = policy({
      updatedAt: "2026-09-07T00:00:00Z",
      updatedBy: "admin-a",
    });
    const metadataOnly = policy({
      updatedAt: "2026-09-07T01:00:00Z",
      updatedBy: "admin-b",
    });
    const changed = policy({ dmlMaxAffectedRows: 250 });

    expect(securityPolicyFingerprint(original)).toBe(
      securityPolicyFingerprint(metadataOnly),
    );
    expect(securityPolicyFingerprint(original)).not.toBe(
      securityPolicyFingerprint(changed),
    );
  });
});
