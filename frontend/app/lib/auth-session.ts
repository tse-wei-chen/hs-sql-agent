export interface AuthSessionPayload {
  accessToken?: string | null
  refreshToken?: string | null
  permissions?: unknown
  email?: string | null
  userName?: string | null
}

export function persistAuthSession(
  payload: AuthSessionPayload,
  storage: Storage = localStorage,
) {
  if (payload.accessToken) storage.setItem("accessToken", payload.accessToken)
  if (payload.refreshToken) storage.setItem("refreshToken", payload.refreshToken)
  if (Array.isArray(payload.permissions)) {
    storage.setItem("permissions", JSON.stringify(payload.permissions))
  }
  if (payload.email) storage.setItem("userEmail", payload.email)
  if (payload.userName) storage.setItem("userName", payload.userName)
}

export function clearAuthSession(storage: Storage = localStorage) {
  storage.removeItem("accessToken")
  storage.removeItem("refreshToken")
  storage.removeItem("permissions")
  storage.removeItem("userEmail")
  storage.removeItem("userName")
}
