import { xiorInstanceToken } from "./xiorInstance";

export interface DbSemantic {
  id: number;
  dbManagementId: number;
  schemaName: string;
  tableName: string;
  columnName?: string | null;
  description?: string | null;
  displayName?: string | null;
}

export interface DbSemanticRequest {
  dbManagementId: number;
  schemaName: string;
  tableName: string;
  columnName?: string | null;
  description?: string | null;
  displayName?: string | null;
}

export const getSemanticsByDbId = async (dbId: number) => {
  const response = await xiorInstanceToken.get<DbSemantic[]>(`/DbSemantic/${dbId}`);
  return response.data;
};

export const upsertSemantic = async (data: DbSemanticRequest) => {
  const response = await xiorInstanceToken.post<DbSemantic>("/DbSemantic", data);
  return response.data;
};

export const deleteSemantic = async (id: number) => {
  const response = await xiorInstanceToken.delete(`/DbSemantic/${id}`);
  return response.data;
};
