import { xiorInstanceToken } from "./xiorInstance";

export interface CustomSqlTool {
  id: number;
  name: string;
  description: string;
  sqlTemplate: string;
  type: "Query" | "DML";
  parametersJson?: string | null;
  dbManagementId?: number | null;
  status: "Draft" | "Published" | "Disabled";
  publishedRevisionId?: number | null;
  createdAt: string;
  lastModifiedAt?: string | null;
}

export const listCustomSqlTools = async (): Promise<CustomSqlTool[]> => {
  const response = await xiorInstanceToken.get("/CustomSqlTool");
  return response.data;
};

export const getCustomSqlTool = async (id: number): Promise<CustomSqlTool> => {
  const response = await xiorInstanceToken.get(`/CustomSqlTool/${id}`);
  return response.data;
};

export const createCustomSqlTool = async (
  payload: Partial<CustomSqlTool>,
): Promise<CustomSqlTool> => {
  const response = await xiorInstanceToken.post("/CustomSqlTool", payload);
  return response.data;
};

export const updateCustomSqlTool = async (
  id: number,
  payload: Partial<CustomSqlTool>,
): Promise<CustomSqlTool> => {
  const response = await xiorInstanceToken.put(`/CustomSqlTool/${id}`, payload);
  return response.data;
};

export const deleteCustomSqlTool = async (id: number): Promise<void> => {
  await xiorInstanceToken.delete(`/CustomSqlTool/${id}`);
};

export interface CustomSqlToolRevision {
  id: number;
  customSqlToolId: number;
  revisionNumber: number;
  dbManagementId: number;
  name: string;
  description: string;
  sqlTemplate: string;
  type: "Query" | "DML";
  parametersJson?: string | null;
  diffJson: string;
  publishedBy?: string | null;
  publishedAt: string;
}

export const listCustomSqlToolRevisions = async (id: number): Promise<CustomSqlToolRevision[]> => {
  const response = await xiorInstanceToken.get(`/CustomSqlTool/${id}/revisions`);
  return response.data;
};

export interface CustomSqlToolImpact {
  toolId: number;
  draftDbManagementId?: number | null;
  draftDatabaseName?: string | null;
  publishedDbManagementId?: number | null;
  publishedDatabaseName?: string | null;
  currentlyExposedToKeys: Array<{ id: number; name: string; keyPrefix: string }>;
  wouldExposeToKeys: Array<{ id: number; name: string; keyPrefix: string }>;
  breakingChanges: string[];
  sqlChanged: boolean;
}

export const getCustomSqlToolImpact = async (id: number): Promise<CustomSqlToolImpact> => {
  const response = await xiorInstanceToken.get(`/CustomSqlTool/${id}/impact`);
  return response.data;
};

export const publishCustomSqlTool = async (id: number): Promise<CustomSqlTool> => {
  const response = await xiorInstanceToken.post(`/CustomSqlTool/${id}/publish`);
  return response.data;
};

export const disableCustomSqlTool = async (id: number): Promise<CustomSqlTool> => {
  const response = await xiorInstanceToken.post(`/CustomSqlTool/${id}/disable`);
  return response.data;
};

export const rollbackCustomSqlTool = async (id: number, revisionId: number): Promise<CustomSqlTool> => {
  const response = await xiorInstanceToken.post(`/CustomSqlTool/${id}/rollback/${revisionId}`);
  return response.data;
};

export interface TestExecuteRequest {
  toolId: number;
  parameters?: Record<string, string | number | boolean | null>;
}

export interface TestExecuteResult {
  success: boolean;
  data?: string;
  error?: string;
}

export const testExecuteCustomSqlTool = async (
  payload: TestExecuteRequest,
): Promise<TestExecuteResult> => {
  const response = await xiorInstanceToken.post(
    "/CustomSqlTool/test-execute",
    payload,
  );
  return response.data;
};
