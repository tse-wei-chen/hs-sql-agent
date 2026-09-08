import { describe, expect, it } from "vitest";
import type { RuntimeDoctorResponse } from "@/api/doctor";
import { buildSystemReadinessSteps } from "./systemReadiness";

const doctor = (
  status: "Healthy" | "Warning" | "Error",
): RuntimeDoctorResponse => ({
  overallStatus: status,
  environment: "Production",
  deploymentMode: "SingleNode",
  generatedAtUtc: "2026-09-08T00:00:00Z",
  checks:
    status === "Healthy"
      ? [{ id: "security.jwt", category: "Security", status: "Healthy", title: "JWT", detail: "ok" }]
      : [{ id: "security.jwt", category: "Security", status, title: "JWT", detail: status.toLowerCase() }],
});

describe("system readiness", () => {
  it("builds the complete launch path from doctor through first request", () => {
    const steps = buildSystemReadinessSteps({
      includeConfiguration: true,
      doctor: doctor("Healthy"),
      databaseCount: 1,
      activeKeyCount: 1,
      mcpEndpoint: "https://sql.example.com/mcp",
      hasObservedAgentRequest: true,
    });

    expect(steps.map((step) => step.id)).toEqual([
      "configuration",
      "database",
      "endpoint",
      "key",
      "request",
    ]);
    expect(steps.every((step) => step.complete)).toBe(true);
  });

  it("treats warnings as reviewable but errors as launch blockers", () => {
    const warningStep = buildSystemReadinessSteps({
      includeConfiguration: true,
      doctor: doctor("Warning"),
      databaseCount: 1,
      activeKeyCount: 1,
      mcpEndpoint: "https://sql.example.com/mcp",
      hasObservedAgentRequest: false,
    })[0];
    const errorStep = buildSystemReadinessSteps({
      includeConfiguration: true,
      doctor: doctor("Error"),
      databaseCount: 1,
      activeKeyCount: 1,
      mcpEndpoint: "https://sql.example.com/mcp",
      hasObservedAgentRequest: false,
    })[0];

    expect(warningStep?.complete).toBe(true);
    expect(warningStep?.description).toContain("warning");
    expect(errorStep?.complete).toBe(false);
    expect(errorStep?.description).toContain("blocking");
  });

  it("keeps endpoint and request readiness independently visible", () => {
    const steps = buildSystemReadinessSteps({
      includeConfiguration: false,
      databaseCount: 1,
      activeKeyCount: 1,
      mcpEndpoint: "",
      hasObservedAgentRequest: false,
    });

    expect(steps.map((step) => step.id)).toEqual([
      "database",
      "endpoint",
      "key",
      "request",
    ]);
    expect(steps.find((step) => step.id === "endpoint")?.complete).toBe(false);
    expect(steps.find((step) => step.id === "request")?.complete).toBe(false);
  });
});
