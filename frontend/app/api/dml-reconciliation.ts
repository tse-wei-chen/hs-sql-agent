import { xiorInstanceToken } from "./xiorInstance";

export type DmlReconciliationResolution = "committed" | "not_committed" | "indeterminate";

export interface DmlReconciliationItem {
  requestId: string;
  status: string;
  updatedAt: string;
  createdAt: string;
  dbManagementId: number;
  databaseProvider: string;
  databaseIdentity: string;
  statementCount: number;
  commitEvidence: string;
  allowedResolutions: DmlReconciliationResolution[];
}

export interface DmlReconciliationRequest {
  expectedStatus: string;
  expectedUpdatedAt: string;
  resolution: DmlReconciliationResolution;
  evidenceReference: string;
  note: string;
}

export const listDmlReconciliation = async (
  limit = 50,
): Promise<DmlReconciliationItem[]> =>
  (await xiorInstanceToken.get<DmlReconciliationItem[]>(
    "/runtime/dml-approvals/reconciliation",
    { params: { limit } },
  )).data;

export const resolveDmlReconciliation = async (
  requestId: string,
  request: DmlReconciliationRequest,
): Promise<DmlReconciliationItem> =>
  (await xiorInstanceToken.post<DmlReconciliationItem>(
    `/runtime/dml-approvals/reconciliation/${encodeURIComponent(requestId)}/resolve`,
    request,
  )).data;
