import { xiorInstanceToken } from "./xiorInstance";

export interface DbManagement {
  id: number;
  name: string;
  sqlProvider?: string | null;
  host?: string | null;
  port?: string | null;
  username?: string | null;
  passwordHash?: string | null;
  database?: string | null;
  extraSettings?: string | null;
  createdAt: string;
  createdBy?: string | null;
  updatedAt: string;
  updatedBy?: string | null;

  visible?: boolean; // For UI purposes, not from backend
}

export interface DbManagementRequest {
  name: string;
  sqlProvider: string;
  host: string;
  port: string;
  username: string;
  password?: string;
  database: string;
  extraSettings?: string;
  createdBy?: string;
  updatedBy?: string;
}

export const listDbManagements = async (): Promise<DbManagement[]> => {
  const response = await xiorInstanceToken.get("/DbManagement");
  return response.data;
};

export const getDbManagement = async (id: number): Promise<DbManagement> => {
  const response = await xiorInstanceToken.get(`/DbManagement/${id}`);
  return response.data;
};

export const createDbManagement = async (
  payload: DbManagementRequest,
): Promise<DbManagement> => {
  const response = await xiorInstanceToken.post("/DbManagement", payload);
  return response.data;
};

export const updateDbManagement = async (
  id: number,
  payload: DbManagementRequest,
): Promise<void> => {
  await xiorInstanceToken.put(`/DbManagement/${id}`, payload);
};

export const deleteDbManagement = async (id: number): Promise<void> => {
  await xiorInstanceToken.delete(`/DbManagement/${id}`);
};

export const getSchemas = async (id: number): Promise<string[]> => {
  const response = await xiorInstanceToken.get(`/DbManagement/${id}/schemas`);
  return response.data;
};

export const getTables = async (
  id: number,
  schema: string = "",
): Promise<string[]> => {
  const response = await xiorInstanceToken.get(`/DbManagement/${id}/tables`, {
    params: { schema },
  });
  return response.data;
};

export const getColumns = async (
  id: number,
  table: string,
  schema: string = "",
): Promise<{ column: string; type: string }[]> => {
  const response = await xiorInstanceToken.get(`/DbManagement/${id}/columns`, {
    params: { schema, table },
  });
  return response.data;
};
