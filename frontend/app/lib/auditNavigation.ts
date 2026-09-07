export interface AuditDrillDownFilters {
  from?: string;
  to?: string;
  dbManagementId?: number;
  accessKeyId?: number;
  toolName?: string;
}

const firstQueryValue = (value: unknown): string | undefined => {
  if (Array.isArray(value)) return firstQueryValue(value[0]);
  if (typeof value !== "string") return undefined;
  const trimmed = value.trim();
  return trimmed || undefined;
};

const positiveInteger = (value: unknown): number | undefined => {
  const text = firstQueryValue(value);
  if (!text || !/^\d+$/.test(text)) return undefined;
  const parsed = Number(text);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : undefined;
};

const dateOnly = (value: unknown): string | undefined => {
  const text = firstQueryValue(value);
  return text && /^\d{4}-\d{2}-\d{2}$/.test(text) ? text : undefined;
};

export function buildAuditDrillDownQuery(
  filters: AuditDrillDownFilters,
): Record<string, string> {
  const query: Record<string, string> = {};
  if (filters.from) query.from = filters.from;
  if (filters.to) query.to = filters.to;
  if (filters.dbManagementId && filters.dbManagementId > 0) {
    query.dbManagementId = String(filters.dbManagementId);
  }
  if (filters.accessKeyId && filters.accessKeyId > 0) {
    query.accessKeyId = String(filters.accessKeyId);
  }
  if (filters.toolName?.trim()) query.toolName = filters.toolName.trim();
  return query;
}

export function parseAuditDrillDownQuery(
  query: Record<string, unknown>,
): AuditDrillDownFilters {
  return {
    from: dateOnly(query.from),
    to: dateOnly(query.to),
    dbManagementId: positiveInteger(query.dbManagementId),
    accessKeyId: positiveInteger(query.accessKeyId),
    toolName: firstQueryValue(query.toolName),
  };
}
