import { xiorInstanceToken } from "./xiorInstance";

export interface SecurityPolicy {
  queryMaxRows: number;
  queryTimeoutSeconds: number;
  requireWhereForUpdate: boolean;
  requireWhereForDelete: boolean;
  allowFullTableUpdate: boolean;
  allowFullTableDelete: boolean;
  dmlMaxAffectedRows: number;
  ipPermitLimit: number;
  ipWindowSeconds: number;
  keyPermitLimit: number;
  keyWindowSeconds: number;
  maxConcurrentSql: number;
  updatedAt?: string | null;
  updatedBy?: string | null;
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
