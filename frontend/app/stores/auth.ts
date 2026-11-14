import { computed, ref, watch } from "vue"
import { defineStore } from "pinia"
import {
  exchangeOidcCode,
  signIn as requestSignIn,
  signOut as requestSignOut,
  signUp as requestSignUp,
  verifyMfa as requestVerifyMfa,
} from "@/api/auth"
import {
  authSessionRevision,
  clearAuthSession,
  persistAuthSession,
  readAuthSession,
  type AuthSessionPayload,
} from "@/lib/auth-session"
import type { PermissionGrant } from "@/lib/permissions"

export type SignInOutcome = "authenticated" | "mfa" | "invalid"

// Route navigation is only a client hint. Backend API permissions and token
// validation remain the authority. Invalid local claims MUST fail closed.
export function parseLocalClaims(token: string | null): Record<string, unknown> | null {
  if (!token) return null
  try {
    const segments = token.split(".")
    if (segments.length !== 3 || !segments[1]) return null
    const parsed: unknown = JSON.parse(atob(segments[1].replace(/-/g, "+").replace(/_/g, "/")))
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : null
  } catch {
    return null
  }
}

function parseGrants(raw: string | null): PermissionGrant[] {
  if (!raw) return []
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed) || !parsed.every((grant) =>
      grant !== null && typeof grant === "object"
      && typeof grant.path === "string"
      && Array.isArray(grant.actions)
      && grant.actions.every((action: unknown) =>
        action !== null && typeof action === "object"
        && typeof (action as { code?: unknown }).code === "string"))) {
      return []
    }
    return parsed as PermissionGrant[]
  } catch {
    return []
  }
}

export const useAuthStore = defineStore("auth", () => {
  const accessToken = ref<string | null>(null)
  const permissions = ref<PermissionGrant[]>([])
  const userName = ref("")
  const userEmail = ref("")

  const claims = computed(() => parseLocalClaims(accessToken.value))
  const isAuthenticated = computed(() => claims.value !== null)

  // A storage projection, never an independent place to persist secrets.
  // The low-level auth-session adapter is also used by HTTP interceptors,
  // which run outside the Vue component/injection context.
  function hydrate() {
    if (typeof localStorage === "undefined") return
    const session = readAuthSession()
    accessToken.value = session.accessToken
    permissions.value = parseGrants(session.permissions)
    userName.value = session.userName || ""
    userEmail.value = session.email || ""
  }

  watch(authSessionRevision, hydrate, { flush: "sync" })
  hydrate()

  function persist(payload: AuthSessionPayload) {
    persistAuthSession(payload)
  }

  function clear() {
    clearAuthSession()
    if (typeof sessionStorage !== "undefined") sessionStorage.removeItem("mfaToken")
  }

  function completeTokens(response: AuthSessionPayload) {
    if (!response?.accessToken || !response?.refreshToken)
      return false
    persist(response)
    if (typeof sessionStorage !== "undefined") sessionStorage.removeItem("mfaToken")
    return true
  }

  function beginMfa(token: string) {
    if (typeof sessionStorage === "undefined") return
    sessionStorage.setItem("mfaToken", token)
  }

  async function login(email: string, password: string): Promise<SignInOutcome> {
    const response = await requestSignIn(email.trim(), password)
    if (completeTokens(response)) return "authenticated"
    if (response?.requiresMfa && response?.mfaToken) {
      beginMfa(response.mfaToken)
      return "mfa"
    }
    return "invalid"
  }

  async function register(email: string, password: string): Promise<boolean> {
    return completeTokens(await requestSignUp(email.trim(), password))
  }

  async function completeMfa(code: string): Promise<void> {
    const challenge = typeof sessionStorage === "undefined"
      ? null : sessionStorage.getItem("mfaToken")
    if (!challenge) throw new Error("MFA challenge expired.")
    if (!completeTokens(await requestVerifyMfa(challenge, code)))
      throw new Error("MFA verification returned no credentials.")
  }

  async function completeSso(code: string): Promise<SignInOutcome> {
    const response = await exchangeOidcCode(code)
    if (completeTokens(response)) return "authenticated"
    if (response?.requiresMfa && response?.mfaToken) {
      beginMfa(response.mfaToken)
      return "mfa"
    }
    return "invalid"
  }

  async function logout() {
    try {
      await requestSignOut()
    } finally {
      clear()
    }
  }

  return {
    accessToken, permissions, userName, userEmail, claims,
    isAuthenticated, hydrate, persist, clear,
    login, register, completeMfa, completeSso, logout,
  }
})
