import { describe, expect, it } from "vitest";
import type { RuntimeDoctorResponse } from "@/api/doctor";
import {
  doctorStatusClass,
  doctorSummary,
  groupDoctorChecks,
} from "./runtimeDoctorPresentation";

const result: RuntimeDoctorResponse = {
  overallStatus: "Warning",
  environment: "Production",
  deploymentMode: "Mixed",
  generatedAtUtc: "2026-09-08T00:00:00Z",
  checks: [
    {
      id: "security.jwt",
      category: "Security",
      status: "Healthy",
      title: "JWT",
      detail: "configured",
    },
    {
      id: "coordination.mode",
      category: "Coordination",
      status: "Warning",
      title: "Coordination",
      detail: "mixed",
    },
    {
      id: "approval.provider",
      category: "DML Approval",
      status: "Error",
      title: "Approval",
      detail: "missing secret",
    },
  ],
};

describe("runtime doctor presentation", () => {
  it("summarizes status counts", () => {
    expect(doctorSummary(result)).toEqual({ errors: 1, warnings: 1, healthy: 1 });
  });

  it("groups checks without losing their category", () => {
    const groups = groupDoctorChecks(result.checks);
    expect(groups.Security?.[0]?.id).toBe("security.jwt");
    expect(groups.Coordination?.[0]?.id).toBe("coordination.mode");
    expect(groups["DML Approval"]?.[0]?.id).toBe("approval.provider");
  });

  it("uses distinct posture classes", () => {
    expect(doctorStatusClass("Healthy")).toContain("emerald");
    expect(doctorStatusClass("Warning")).toContain("amber");
    expect(doctorStatusClass("Error")).toContain("red");
  });
});
