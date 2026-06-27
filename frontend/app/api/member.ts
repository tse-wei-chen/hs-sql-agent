import { xiorInstanceToken } from "./xiorInstance";

export interface Member {
  id: number;
  username: string;
  mail: string;
  roleIds?: number[];
  roles?: string[];
}
export interface CreateMemberPayload {
  email: string;
  username?: string;
  password: string;
  assignAllRoles: boolean;
  roleIds: number[];
}

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
