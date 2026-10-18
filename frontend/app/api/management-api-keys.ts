import { xiorInstanceToken } from "./xiorInstance";

export interface ManagementApiKey {
  id: number;
  name: string;
  keyPrefix: string;
  permissions: string[];
  createdAt: string;
  expiresAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
  createdBy: string | null;
}

export interface IssuedManagementApiKey {
  key: ManagementApiKey;
  plaintextKey: string;
}

export const listManagementApiKeys = async () =>
  (await xiorInstanceToken.get<ManagementApiKey[]>("/runtime/management-api-keys")).data;

export const listManagementKeyPermissions = async () =>
  (await xiorInstanceToken.get<string[]>("/runtime/management-api-keys/permissions")).data;

export const issueManagementApiKey = async (payload: {
  name: string; expiresAt: string; permissions: string[];
}) => (await xiorInstanceToken.post<IssuedManagementApiKey>("/runtime/management-api-keys", payload)).data;

export const rotateManagementApiKey = async (id: number) =>
  (await xiorInstanceToken.post<IssuedManagementApiKey>(`/runtime/management-api-keys/${id}/rotate`)).data;

export const revokeManagementApiKey = async (id: number) =>
  xiorInstanceToken.post(`/runtime/management-api-keys/${id}/revoke`);
