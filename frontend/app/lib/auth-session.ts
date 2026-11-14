import { ref } from "vue"

export const authSessionRevision = ref(0)

export interface AuthSessionPayload {
  accessToken?: string | null
  refreshToken?: string | null
  permissions?: unknown
  email?: string | null
  userName?: string | null
}

// Single storage adapter for the Pinia projection, HTTP refresh interceptor and
// account-revocation paths. Never read auth storage directly in UI components.
export function readAuthSession(storage: Storage = localStorage) {
  return {
    accessToken: storage.getItem("accessToken"),
    refreshToken: storage.getItem("refreshToken"),
    permissions: storage.getItem("permissions"),
    email: storage.getItem("userEmail"),
    userName: storage.getItem("userName"),
  }
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
  authSessionRevision.value += 1
}

export function clearAuthSession(storage: Storage = localStorage) {
  storage.removeItem("accessToken")
  storage.removeItem("refreshToken")
  storage.removeItem("permissions")
  storage.removeItem("userEmail")
  storage.removeItem("userName")
  authSessionRevision.value += 1
}
