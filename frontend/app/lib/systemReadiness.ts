import type { RuntimeDoctorResponse } from "@/api/doctor";

export interface SystemReadinessInput {
  includeConfiguration: boolean;
  doctor?: RuntimeDoctorResponse | null;
  databaseCount: number;
  activeKeyCount: number;
  mcpEndpoint?: string | null;
  hasObservedAgentRequest: boolean;
}

export interface SystemReadinessStep {
  id: string;
  title: string;
  description: string;
  complete: boolean;
  to: string;
  action: string;
}

export const buildSystemReadinessSteps = (
  input: SystemReadinessInput,
): SystemReadinessStep[] => {
  const steps: SystemReadinessStep[] = [];

  if (input.includeConfiguration) {
    const errors = input.doctor?.checks.filter((check) => check.status === "Error").length;
    const warnings = input.doctor?.checks.filter((check) => check.status === "Warning").length;
    const complete = input.doctor != null && (errors ?? 0) === 0;
    const description = !input.doctor
      ? "Run Configuration Doctor to validate deployment and security prerequisites."
      : (errors ?? 0) > 0
        ? `${errors} blocking configuration issue(s) must be fixed before launch.`
        : (warnings ?? 0) > 0
          ? `No blocking configuration errors; ${warnings} warning(s) still deserve operator review.`
          : "Configuration Doctor reports a healthy deployment posture.";

    steps.push({
      id: "configuration",
      title: "Clear deployment blockers",
      description,
      complete,
      to: "/runtime/operability",
      action: complete ? "Review doctor" : "Run doctor",
    });
  }

  steps.push({
    id: "database",
    title: "Add a database connection",
    description: "Register and test at least one governed database target.",
    complete: input.databaseCount > 0,
    to: "/runtime/db-management",
    action: input.databaseCount > 0 ? "Manage databases" : "Add database",
  });

  const endpoint = input.mcpEndpoint?.trim() ?? "";
  steps.push({
    id: "endpoint",
    title: "Verify the MCP public endpoint",
    description: endpoint
      ? `Client endpoint: ${endpoint}`
      : "The externally reachable MCP endpoint is not available to client onboarding yet.",
    complete: endpoint.length > 0,
    to: endpoint ? "/runtime/mcp-keys" : "/runtime/operability",
    action: endpoint ? "Review client setup" : "Review endpoint",
  });

  steps.push({
    id: "key",
    title: "Issue an MCP access key",
    description: "Start with the four default read/query tools; enable DML only when needed.",
    complete: input.activeKeyCount > 0,
    to: "/runtime/mcp-keys",
    action: input.activeKeyCount > 0 ? "Manage keys" : "Issue key",
  });

  steps.push({
    id: "request",
    title: "Run the first governed request",
    description: "Connect Claude, Cursor, VS Code, or another MCP client and complete one governed request.",
    complete: input.hasObservedAgentRequest,
    to: "/runtime/mcp-keys",
    action: input.hasObservedAgentRequest ? "View key usage" : "Connect client",
  });

  return steps;
};
