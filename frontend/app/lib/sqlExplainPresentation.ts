import type {
  SqlExplainAccessKeyOption,
  SqlExplainContext,
  SqlExplainDatabaseOption,
  SqlExplainResult,
} from "@/api/security";

export const GLOBAL_POLICY_LABEL = "Global policy only";

export const databaseLabel = (database: SqlExplainDatabaseOption) =>
  `${database.name} · ${database.sqlProvider} · #${database.id}`;

export const accessKeyLabel = (key: SqlExplainAccessKeyOption) =>
  `${key.name} · #${key.id}${!key.isActive || key.isExpired ? " · inactive" : ""}`;

export const databaseOptions = (context: SqlExplainContext) =>
  context.databases.map(databaseLabel);

export const accessKeyOptions = (context: SqlExplainContext, dbManagementId?: number) => [
  GLOBAL_POLICY_LABEL,
  ...context.accessKeys
    .filter(key => key.dbManagementId === dbManagementId)
    .map(accessKeyLabel),
];

export const resolveDatabase = (
  context: SqlExplainContext,
  label: string,
): SqlExplainDatabaseOption | undefined =>
  context.databases.find(database => databaseLabel(database) === label);

export const resolveAccessKey = (
  context: SqlExplainContext,
  label: string,
): SqlExplainAccessKeyOption | undefined =>
  label === GLOBAL_POLICY_LABEL
    ? undefined
    : context.accessKeys.find(key => accessKeyLabel(key) === label);

export const explainOutcome = (result: SqlExplainResult) => {
  if (result.success) {
    return {
      label: "Translated",
      tone: "success" as const,
      detail: `${result.sourceDialect} → ${result.targetProvider}`,
    };
  }

  return {
    label: result.failure?.stage === "Authorization" ? "Denied" : "Rejected",
    tone: "danger" as const,
    detail: result.failure
      ? `${result.failure.stage} · ${result.failure.code}`
      : "Simulation rejected",
  };
};

export const formatParameterValue = (value: unknown) => {
  if (typeof value === "string") return JSON.stringify(value);
  if (value === null) return "null";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
};
