import { describe, expect, it } from "vitest";
import {
  buildDatabaseFilterOptions,
  buildKeyFilterOptions,
  resolveOperabilityFilterId,
} from "./operabilityFilters";

describe("operability filters", () => {
  it("builds readable database labels while preserving the numeric id", () => {
    const options = buildDatabaseFilterOptions([
      { dbManagementId: 12, name: "Orders", provider: "PostgreSQL" },
    ]);

    expect(options).toEqual([
      { id: null, label: "All databases" },
      { id: 12, label: "Orders · PostgreSQL · #12" },
    ]);
    expect(resolveOperabilityFilterId("Orders · PostgreSQL · #12", options)).toBe(12);
    expect(resolveOperabilityFilterId("All databases", options)).toBeUndefined();
  });

  it("builds readable key labels without requiring MCP-key management data", () => {
    const options = buildKeyFilterOptions([
      { accessKeyId: 7, name: "Claude Production" },
    ]);

    expect(options).toEqual([
      { id: null, label: "All keys" },
      { id: 7, label: "Claude Production · #7" },
    ]);
    expect(resolveOperabilityFilterId("Claude Production · #7", options)).toBe(7);
    expect(resolveOperabilityFilterId("All keys", options)).toBeUndefined();
  });

  it("fails closed to no filter for labels that are not in the constrained option set", () => {
    const options = buildKeyFilterOptions([{ accessKeyId: 3, name: "Known" }]);
    expect(resolveOperabilityFilterId("typed arbitrary value", options)).toBeUndefined();
  });
});
