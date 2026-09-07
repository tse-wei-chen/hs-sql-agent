export interface OperabilityFilterOption {
  id: number | null;
  label: string;
}

export interface OperabilityDatabaseOptionSource {
  dbManagementId: number;
  name?: string | null;
  provider?: string | null;
}

export interface OperabilityKeyOptionSource {
  accessKeyId: number;
  name?: string | null;
}

export function buildDatabaseFilterOptions(
  databases: readonly OperabilityDatabaseOptionSource[],
): OperabilityFilterOption[] {
  return [
    { id: null, label: "All databases" },
    ...databases.map((database) => ({
      id: database.dbManagementId,
      label: `${database.name || `Database #${database.dbManagementId}`} · ${database.provider || "Unknown provider"} · #${database.dbManagementId}`,
    })),
  ];
}

export function buildKeyFilterOptions(
  keys: readonly OperabilityKeyOptionSource[],
): OperabilityFilterOption[] {
  return [
    { id: null, label: "All keys" },
    ...keys.map((key) => ({
      id: key.accessKeyId,
      label: `${key.name || `Key #${key.accessKeyId}`} · #${key.accessKeyId}`,
    })),
  ];
}

export function resolveOperabilityFilterId(
  selectedLabel: string,
  options: readonly OperabilityFilterOption[],
): number | undefined {
  const match = options.find((option) => option.label === selectedLabel);
  return match?.id ?? undefined;
}
