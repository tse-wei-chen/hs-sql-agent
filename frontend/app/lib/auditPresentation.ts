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
