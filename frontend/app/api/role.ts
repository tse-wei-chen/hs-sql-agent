import { xiorInstanceToken } from "./xiorInstance";
export interface Role {
  id: number;
  name: string;
  description?: string | null;
  permissionActions?: PermissionActionSelection[];
}

export interface RolePayload {
  name: string;
  description?: string | null;
  permissionActions: PermissionActionSelection[];
}

export interface PermissionActionSelection {
  permissionId: number;
  actionId: number;
}

export interface PermissionActionTemplate {
  id: number;
  permission: Permission;
  action: AuthAction;
}

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

export const deleteRole = async (id: number, force = false) => {
  await xiorInstanceToken.delete(`/role/${id}`, { params: { force } });
};

export interface RoleDependencies { roleId: number; roleName: string; permissions: string[]; members: { id: number; username: string; mail: string }[] }
export const getRoleDependencies = async (id: number) => {
  const response = await xiorInstanceToken.get<RoleDependencies>(`/role/${id}/dependencies`);
  return response.data;
};

export const listPermissionActionTemplates = async () => {
  const response = await xiorInstanceToken.get<PermissionActionTemplate[]>("/role/permission-action-templates");
  return response.data;
};
