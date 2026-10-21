export interface AuditExecutionSummaryInput {
  durationMs?: number | null;
  returnedRows?: number | null;
  affectedRows?: number | null;
  approvalStatus?: string | null;
}

export function formatAuditDefinition(value?: string | null): string {
  if (!value) return "";

  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

export function buildAuditExecutionSummary(
  item: AuditExecutionSummaryInput,
): string[] {
  const summary: string[] = [];

  if (item.durationMs != null) summary.push(`${item.durationMs} ms`);
  if (item.returnedRows != null) summary.push(`${item.returnedRows} returned`);
  if (item.affectedRows != null) summary.push(`${item.affectedRows} affected`);
  if (item.approvalStatus) summary.push(`approval ${item.approvalStatus}`);

  return summary;
}

/**
 * A missing COMMIT acknowledgement is not proof of rollback. Audit results
 * use a distinct terminal state so operators don't blindly retry the SQL.
 */
export function isAuditCommitOutcomeUnknown(item: {
  result?: string | null;
  errorCategory?: string | null;
}): boolean {
  return (
    item.result?.toLowerCase() === "outcome_unknown" ||
    item.errorCategory?.toLowerCase() === "dmlcommitoutcomeunknown" ||
    item.errorCategory?.toLowerCase() === "dmlcommitoutcomeunknownexception"
  );
}

export function auditOutcomeLabel(item: {
  result?: string | null;
  errorCategory?: string | null;
}): string {
  return isAuditCommitOutcomeUnknown(item)
    ? "Commit outcome unknown"
    : item.result || "—";
}
