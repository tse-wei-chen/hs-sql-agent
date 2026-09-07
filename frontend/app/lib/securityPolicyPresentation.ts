import type { SecurityPolicy } from "@/api/security";

export type SecurityPolicyPosture = {
  label: "Guarded mutation policy" | "Review mutation policy";
  requiresReview: boolean;
  summary: string;
  warnings: string[];
  facts: Array<{ label: string; value: string }>;
};

const policySnapshot = (policy: SecurityPolicy) => ({
  queryMaxRows: policy.queryMaxRows,
  queryTimeoutSeconds: policy.queryTimeoutSeconds,
  requireWhereForUpdate: policy.requireWhereForUpdate,
  requireWhereForDelete: policy.requireWhereForDelete,
  allowFullTableUpdate: policy.allowFullTableUpdate,
  allowFullTableDelete: policy.allowFullTableDelete,
  dmlMaxAffectedRows: policy.dmlMaxAffectedRows,
  keyPermitLimit: policy.keyPermitLimit,
  keyWindowSeconds: policy.keyWindowSeconds,
  maxConcurrentSql: policy.maxConcurrentSql,
});

export const securityPolicyFingerprint = (policy: SecurityPolicy) =>
  JSON.stringify(policySnapshot(policy));

export const buildSecurityPolicyPosture = (
  policy: SecurityPolicy,
): SecurityPolicyPosture => {
  const warnings: string[] = [];

  if (!policy.requireWhereForUpdate) {
    warnings.push("UPDATE can run without a WHERE clause.");
  }
  if (!policy.requireWhereForDelete) {
    warnings.push("DELETE can run without a WHERE clause.");
  }
  if (policy.allowFullTableUpdate) {
    warnings.push("Full-table UPDATE is allowed by the current policy.");
  }
  if (policy.allowFullTableDelete) {
    warnings.push("Full-table DELETE is allowed by the current policy.");
  }

  const requiresReview = warnings.length > 0;
  const whereSummary = [
    policy.requireWhereForUpdate ? "UPDATE required" : "UPDATE optional",
    policy.requireWhereForDelete ? "DELETE required" : "DELETE optional",
  ].join(" · ");
  const fullTableSummary = [
    policy.allowFullTableUpdate ? "UPDATE allowed" : "UPDATE blocked",
    policy.allowFullTableDelete ? "DELETE allowed" : "DELETE blocked",
  ].join(" · ");

  return {
    label: requiresReview ? "Review mutation policy" : "Guarded mutation policy",
    requiresReview,
    summary: requiresReview
      ? "One or more mutation guardrails are relaxed. Review the effective policy before enabling DML for agents."
      : "WHERE is required for UPDATE/DELETE and full-table mutation is blocked.",
    warnings,
    facts: [
      { label: "WHERE policy", value: whereSummary },
      { label: "Full-table mutation", value: fullTableSummary },
      { label: "DML row cap", value: `${policy.dmlMaxAffectedRows} rows` },
      {
        label: "Query limit",
        value: `${policy.queryMaxRows} rows · ${policy.queryTimeoutSeconds}s timeout`,
      },
      {
        label: "Key rate limit",
        value: `${policy.keyPermitLimit} requests / ${policy.keyWindowSeconds}s`,
      },
      {
        label: "SQL concurrency",
        value: `${policy.maxConcurrentSql} operations`,
      },
    ],
  };
};
