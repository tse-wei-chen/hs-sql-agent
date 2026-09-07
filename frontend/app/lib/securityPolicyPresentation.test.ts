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
  it("reports a guarded mutation policy when both mutations require predicates", () => {
    const result = buildSecurityPolicyPosture(policy());

    expect(result.label).toBe("Guarded mutation policy");
    expect(result.requiresReview).toBe(false);
    expect(result.warnings).toEqual([]);
    expect(result.facts).toContainEqual({
      label: "Effective UPDATE",
      value: "Predicate required",
    });
    expect(result.facts).toContainEqual({
      label: "Effective DELETE",
      value: "Predicate required",
    });
  });

  it("keeps a predicate requirement when only one raw flag is relaxed", () => {
    const result = buildSecurityPolicyPosture(
      policy({
        requireWhereForUpdate: false,
        allowFullTableUpdate: false,
        requireWhereForDelete: true,
        allowFullTableDelete: true,
      }),
    );

    expect(result.label).toBe("Guarded mutation policy");
    expect(result.requiresReview).toBe(false);
    expect(result.warnings).toEqual([]);
    expect(result.facts).toContainEqual({
      label: "Effective UPDATE",
      value: "Predicate required",
    });
    expect(result.facts).toContainEqual({
      label: "Effective DELETE",
      value: "Predicate required",
    });
  });

  it("flags only mutation paths whose two policy flags jointly allow all rows", () => {
    const result = buildSecurityPolicyPosture(
      policy({
        requireWhereForUpdate: false,
        allowFullTableUpdate: true,
        requireWhereForDelete: false,
        allowFullTableDelete: true,
      }),
    );

    expect(result.label).toBe("Review mutation policy");
    expect(result.requiresReview).toBe(true);
    expect(result.warnings).toEqual([
      "Full-table UPDATE is allowed by the effective compiler policy.",
      "Full-table DELETE is allowed by the effective compiler policy.",
    ]);
    expect(result.facts).toContainEqual({
      label: "Effective UPDATE",
      value: "Full-table allowed",
    });
    expect(result.facts).toContainEqual({
      label: "Effective DELETE",
      value: "Full-table allowed",
    });
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
