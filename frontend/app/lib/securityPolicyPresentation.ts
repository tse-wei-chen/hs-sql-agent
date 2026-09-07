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

const fullTableAllowed = (requireWhere: boolean, allowFullTable: boolean) =>
  !requireWhere && allowFullTable;

export const buildSecurityPolicyPosture = (
  policy: SecurityPolicy,
): SecurityPolicyPosture => {
  const updateAllowsAllRows = fullTableAllowed(
    policy.requireWhereForUpdate,
    policy.allowFullTableUpdate,
  );
  const deleteAllowsAllRows = fullTableAllowed(
    policy.requireWhereForDelete,
    policy.allowFullTableDelete,
  );
  const warnings: string[] = [];

  if (updateAllowsAllRows) {
    warnings.push("Full-table UPDATE is allowed by the effective compiler policy.");
  }
  if (deleteAllowsAllRows) {
    warnings.push("Full-table DELETE is allowed by the effective compiler policy.");
  }

  const requiresReview = warnings.length > 0;

  return {
    label: requiresReview ? "Review mutation policy" : "Guarded mutation policy",
    requiresReview,
    summary: requiresReview
      ? "The effective compiler policy allows at least one full-table mutation path. Review the guardrails before enabling DML for agents."
      : "The effective compiler policy requires a predicate for UPDATE and DELETE.",
    warnings,
    facts: [
      {
        label: "Effective UPDATE",
        value: updateAllowsAllRows ? "Full-table allowed" : "Predicate required",
      },
      {
        label: "Effective DELETE",
        value: deleteAllowsAllRows ? "Full-table allowed" : "Predicate required",
      },
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
