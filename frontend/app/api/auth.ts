import {
  xiorInstance,
  xiorInstanceToken,
  xiorInstanceRefreshToken,
} from "./xiorInstance";

export const checkFirstRun = async () => {
  const response = await xiorInstance.get("/auth/first-run");
  return response.data;
};

export const signIn = async (email: string, password: string) => {
  const response = await xiorInstance.post("/auth/sign-in", { email, password });
  return response.data;
};

export const signUp = async (email: string, password: string) => {
  const response = await xiorInstance.post("/auth/sign-up", { email, password });
  return response.data;
};

export const refreshToken = async () => {
  const response = await xiorInstanceRefreshToken.post("/auth/refresh-token");
  return response.data;
};

export const signOut = async () => {
  const refreshTokenVal = localStorage.getItem("refreshToken");
  const accessTokenVal = localStorage.getItem("accessToken");
  try {
    await xiorInstance.post("/auth/sign-out", {
      refreshToken: refreshTokenVal || undefined,
    }, {
      headers: accessTokenVal
        ? { Authorization: `Bearer ${accessTokenVal}` }
        : undefined,
    });
  } catch {
    // ignore
  }
};

export interface AuthSession {
  id: string;
  isCurrent: boolean;
  createdAt: string;
  lastUsedAt: string;
  expiresAt: string;
  ipAddress?: string | null;
  userAgent?: string | null;
}

export const listSessions = async () => {
  const response = await xiorInstanceToken.get<AuthSession[]>("/auth/sessions");
  return response.data;
};

export const revokeSession = async (sessionId: string) => {
  await xiorInstanceToken.delete(`/auth/sessions/${sessionId}`);
};

export const revokeOtherSessions = async () => {
  await xiorInstanceToken.delete("/auth/sessions");
};

export interface AccountProfile {
  id: number;
  username: string;
  mail: string;
  requirePasswordChangeAtNextSignIn: boolean;
  createdAt: string;
  lastLoginAt?: string | null;
}

export const getAccount = async () => {
  const response = await xiorInstanceToken.get<AccountProfile>("/auth/account");
  return response.data;
};

export const updateAccount = async (username: string, email: string) => {
  const response = await xiorInstanceToken.put<AccountProfile>("/auth/account", { username, email });
  return response.data;
};

export const changePassword = async (currentPassword: string, newPassword: string) => {
  await xiorInstanceToken.put("/auth/account/password", { currentPassword, newPassword });
};

export const forgotPassword = async (email: string) => {
  const response = await xiorInstance.post("/auth/forgot-password", { email });
  return response.data;
};

export const resetPassword = async (token: string, newPassword: string) => {
  await xiorInstance.post("/auth/reset-password", { token, newPassword });
};

export const getOidcStatus = async () => {
  const response = await xiorInstance.get<{ enabled: boolean }>("/auth/oidc/status");
  return response.data;
};

export const exchangeOidcCode = async (code: string) => {
  const response = await xiorInstance.post("/auth/oidc/exchange", { code });
  return response.data;
};

export const verifyMfa = async (mfaToken: string, code: string) => {
  const response = await xiorInstance.post("/auth/mfa/verify", { code }, {
    headers: { Authorization: `Bearer ${mfaToken}` },
  });
  return response.data;
};

export interface MfaStatus { enabled: boolean; recoveryCodesRemaining: number }
export interface MfaSetup { secret: string; otpAuthUri: string }
export const getMfaStatus = async () => (await xiorInstanceToken.get<MfaStatus>("/auth/mfa/status")).data;
export const beginMfaSetup = async () => (await xiorInstanceToken.post<MfaSetup>("/auth/mfa/setup")).data;
export const confirmMfaSetup = async (code: string) => (await xiorInstanceToken.post<{ recoveryCodes: string[] }>("/auth/mfa/confirm", { code })).data;
export const disableMfa = async (code: string) => { await xiorInstanceToken.post("/auth/mfa/disable", { code }); };
