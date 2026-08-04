import { xiorInstanceToken } from "./xiorInstance";

export interface Member {
  id: number;
  username: string;
  mail: string;
  isActive: boolean;
  roleIds?: number[];
  roles?: string[];
  requirePasswordChangeAtNextSignIn: boolean;
  createdAt: string;
  lastLoginAt?: string | null;
  activeSessionCount: number;
}
export interface CreateMemberPayload {
  email: string;
  username?: string;
  password: string;
  assignAllRoles: boolean;
  roleIds: number[];
}

export interface MemberPage { items: Member[]; totalCount: number; page: number; pageSize: number }

export const listMembers = async (params: Record<string, string | number | boolean | undefined> = {}) => {
  const response = await xiorInstanceToken.get<MemberPage>("/member", { params });
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

export const updateMemberStatus = async (id: number, isActive: boolean) => {
  const response = await xiorInstanceToken.put<Member>(`/member/${id}/status`, { isActive });
  return response.data;
};

export const deleteMember = async (id: number) => {
  await xiorInstanceToken.delete(`/member/${id}`);
};

export const revokeMemberSessions = async (id: number) => {
  await xiorInstanceToken.delete(`/member/${id}/sessions`);
};

export const requireMemberPasswordChange = async (id: number, required = true) => {
  await xiorInstanceToken.put(`/member/${id}/password-change-required`, { required });
};
