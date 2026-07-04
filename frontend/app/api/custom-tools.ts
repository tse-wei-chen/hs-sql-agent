import { xiorInstanceToken } from "./xiorInstance";

export interface CustomSqlTool {
  id: number;
  name: string;
  description: string;
  definitionJson: string;
  type: "Query" | "DML";
  parametersJson?: string | null;
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

export interface ParseSqlResult {
  success: boolean;
  data?: string;
  error?: string;
}

export const parseSqlCustomSqlTool = async (
  sql: string,
): Promise<ParseSqlResult> => {
  const response = await xiorInstanceToken.post(
    "/CustomSqlTool/parse-sql",
    { sql },
  );
  return response.data;
};

export interface TestExecuteRequest {
  definitionJson: string;
  type: "Query" | "DML";
  dbId: number;
  parameters?: Record<string, string>;
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
