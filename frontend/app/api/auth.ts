import { xiorInstanceToken } from "./xiorInstance";

export interface AuthAction {
  id: number;
  code: string;
  name: string;
}

export interface Permission {
  id: number;
  name: string;
  path: string;
}

export interface PermissionActionTemplate {
  id: number;
  permission: Permission;
  action: AuthAction;
}

export interface PermissionActionSelection {
  permissionId: number;
  actionId: number;
}

export interface Role {
  id: number;
  name: string;
  description?: string | null;
  permissionActions?: PermissionActionSelection[];
}

export interface Member {
  id: number;
  username: string;
  mail: string;
  roleIds?: number[];
  roles?: string[];
}

export interface RolePayload {
  name: string;
  description?: string | null;
  permissionActions: PermissionActionSelection[];
}

export interface CreateMemberPayload {
  email: string;
  username?: string;
  password: string;
  assignAllRoles: boolean;
  roleIds: number[];
}

export const listRoles = async () => {
  const response = await xiorInstanceToken.get<Role[]>("/role");
  return response.data;
};

export const createRole = async (payload: RolePayload) => {
  const response = await xiorInstanceToken.post<Role>("/role", payload);
  return response.data;
};

export const updateRole = async (id: number, payload: RolePayload) => {
  const response = await xiorInstanceToken.put<Role>(`/role/${id}`, payload);
  return response.data;
};

export const deleteRole = async (id: number) => {
  await xiorInstanceToken.delete(`/role/${id}`);
};

export const listPermissionActionTemplates = async () => {
  const response = await xiorInstanceToken.get<PermissionActionTemplate[]>("/role/permission-action-templates");
  return response.data;
};

export const listMembers = async () => {
  const response = await xiorInstanceToken.get<Member[]>("/member");
  return response.data;
};

export const createMember = async (payload: CreateMemberPayload) => {
  const response = await xiorInstanceToken.post<{ id: number }>("/member", payload);
  return response.data;
};

export const updateMemberRoles = async (id: number, roleIds: number[]) => {
  const response = await xiorInstanceToken.put<Member>(`/member/${id}/roles`, { roleIds });
  return response.data;
};

export const deleteMember = async (id: number) => {
  await xiorInstanceToken.delete(`/member/${id}`);
};
