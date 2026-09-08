import { xiorInstanceToken } from "./xiorInstance";

export interface SecurityPolicy {
  queryMaxRows: number;
  queryTimeoutSeconds: number;
  requireWhereForUpdate: boolean;
  requireWhereForDelete: boolean;
  allowFullTableUpdate: boolean;
  allowFullTableDelete: boolean;
  dmlMaxAffectedRows: number;
  keyPermitLimit: number;
  keyWindowSeconds: number;
  maxConcurrentSql: number;
  updatedAt?: string | null;
  updatedBy?: string | null;
}

export interface SqlExplainDatabaseOption {
  id: number;
  name: string;
  sqlProvider: string;
}

export interface SqlExplainAccessKeyOption {
  id: number;
  name: string;
  dbManagementId: number;
  isActive: boolean;
  isExpired: boolean;
  allowedTools?: string | null;
  tableWhitelist?: string | null;
}

export interface SqlExplainContext {
  databases: SqlExplainDatabaseOption[];
  accessKeys: SqlExplainAccessKeyOption[];
}

export interface SqlExplainRequest {
  dbManagementId: number;
  sql: string;
  statementType: "Query" | "DML";
  sourceDialect?: string | null;
  accessKeyId?: number | null;
}

export interface SqlExplainDiagnostic {
  code: string;
  stage: string;
  category: string;
  message: string;
  start?: number | null;
  length?: number | null;
  end?: number | null;
}

export interface SqlExplainEvidence {
  schemaVersion: string;
  capabilityMatrixVersion: string;
  verdict: string;
  decisionBoundary: string;
  decisionCode: string;
  planFingerprint?: string | null;
  evidenceFingerprint: string;
  sourceProfile: {
    provider: string;
    serverVersion?: string | null;
    compatibilityLevel?: number | null;
    sessionModes: string[];
    sessionSettings: { name: string; value: string }[];
  };
  targetProfile: {
    provider: string;
    serverVersion?: string | null;
    compatibilityLevel?: number | null;
    sessionModes: string[];
    sessionSettings: { name: string; value: string }[];
  };
  policy: {
    policyVersion: string;
    queryMaxRows: number;
    requireUpdatePredicate: boolean;
    requireDeletePredicate: boolean;
    allowedTables: string[];
  };
  sourceCapabilities: SqlExplainCapability[];
  targetCapabilities: SqlExplainCapability[];
  assurances: { kind: string; details: { name: string; value: string }[] }[];
}

export interface SqlExplainCapability {
  side: string;
  id: string;
  category: string;
  status: string;
  detail: string;
}

export interface SqlExplainResult {
  success: boolean;
  simulationMode: string;
  executed: boolean;
  statementType: string;
  sourceDialect: string;
  targetProvider: string;
  scope: {
    accessKeyId?: number | null;
    accessKeyName?: string | null;
    keyActive: boolean;
    requiredTool: string;
    toolAllowed: boolean;
    toolScope: string;
    tableScope: string;
    allowedTables: string[];
  };
  policy: {
    queryMaxRows: number;
    queryTimeoutSeconds: number;
    requireWhereForUpdate: boolean;
    requireWhereForDelete: boolean;
    allowFullTableUpdate: boolean;
    allowFullTableDelete: boolean;
    dmlMaxAffectedRows: number;
    dmlAffectedRowLimitEvaluated: boolean;
  };
  statements: {
    index: number;
    kind: string;
    renderedSql: string;
    returnsRows: boolean;
    planFingerprint: string;
    parameters: { name: string; value: unknown }[];
    queryFacts?: {
      referencedTables: string[];
      containsCte: boolean;
      containsSubquery: boolean;
    } | null;
    evidence?: SqlExplainEvidence | null;
  }[];
  failure?: {
    code: string;
    message: string;
    stage: string;
    retryable: boolean;
    diagnostics: SqlExplainDiagnostic[];
    evidence?: SqlExplainEvidence | null;
  } | null;
}

export const getSecurityPolicy = async (): Promise<SecurityPolicy> => {
  const response = await xiorInstanceToken.get("/runtime/security");
  return response.data;
};

export const updateSecurityPolicy = async (
  policy: SecurityPolicy,
): Promise<SecurityPolicy> => {
  const response = await xiorInstanceToken.put("/runtime/security", policy);
  return response.data;
};

export const getSqlExplainContext = async (): Promise<SqlExplainContext> => {
  const response = await xiorInstanceToken.get<SqlExplainContext>(
    "/runtime/security/sql-explain/context",
  );
  return response.data;
};

export const explainSql = async (
  request: SqlExplainRequest,
): Promise<SqlExplainResult> => {
  const response = await xiorInstanceToken.post<SqlExplainResult>(
    "/runtime/security/sql-explain",
    request,
  );
  return response.data;
};
