import { describe, expect, it } from "vitest";
import {
  buildDbConnectionTestRequest,
  requiresDbPassword,
  type DbConnectionFormValues,
} from "./db-management";

const values: DbConnectionFormValues = {
  sqlProvider: "Postgres",
  host: "new-host",
  port: "5432",
  username: "user",
  password: "",
  database: "app",
  TrustServerCertificate: false,
  Encrypt: true,
};

describe("requiresDbPassword", () => {
  it("requires a password for credential-based providers", () => {
    expect(requiresDbPassword("Postgres")).toBe(true);
    expect(requiresDbPassword("MySQL")).toBe(true);
  });

  it("does not require a password where the form has no credential field", () => {
    expect(requiresDbPassword("Sqlite")).toBe(false);
    expect(requiresDbPassword("Global")).toBe(false);
  });
});

describe("buildDbConnectionTestRequest", () => {
  it("tests the saved connection when an edit keeps the password blank", () => {
    expect(buildDbConnectionTestRequest(values, 42)).toEqual({
      dbSettingMode: 0,
      dbManagementId: 42,
    });
  });

  it("tests edited values when a replacement password is supplied", () => {
    const request = buildDbConnectionTestRequest(
      { ...values, password: "replacement" },
      42,
    );

    expect(request).toMatchObject({
      dbSettingMode: 1,
      dbManagementId: undefined,
      host: "new-host",
      password: "replacement",
    });
  });

  it("tests form values for a new connection", () => {
    expect(buildDbConnectionTestRequest(values, null)).toMatchObject({
      dbSettingMode: 1,
      host: "new-host",
      password: "",
    });
  });
});
