import type { TestDbConnectionRequest } from "@/api/runtime";

export interface DbConnectionFormValues {
  sqlProvider: string;
  host: string;
  port: string;
  username: string;
  password: string;
  database: string;
  TrustServerCertificate: boolean;
  Encrypt: boolean;
}

export const requiresDbPassword = (sqlProvider: string): boolean =>
  !["Sqlite", "Global"].includes(sqlProvider);

export const buildDbConnectionTestRequest = (
  values: DbConnectionFormValues,
  editingId: number | null,
): TestDbConnectionRequest => {
  if (editingId && !values.password.trim()) {
    return {
      dbSettingMode: 0,
      dbManagementId: editingId,
    };
  }

  return {
    dbSettingMode: 1,
    dbManagementId: undefined,
    sqlProvider: values.sqlProvider,
    host: values.host,
    port: values.port,
    username: values.username,
    password: values.password,
    database: values.database,
    extraSettings: JSON.stringify({
      TrustServerCertificate: values.TrustServerCertificate,
      Encrypt: values.Encrypt,
    }),
  };
};
