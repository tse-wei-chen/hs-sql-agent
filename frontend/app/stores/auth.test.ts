import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { createPinia, setActivePinia } from "pinia"
import {
  exchangeOidcCode,
  signIn,
  signOut,
  signUp,
  verifyMfa,
} from "@/api/auth"
import { clearAuthSession, persistAuthSession } from "@/lib/auth-session"
import { useAuthStore } from "./auth"

vi.mock("@/api/auth", () => ({
  exchangeOidcCode: vi.fn(),
  signIn: vi.fn(),
  signOut: vi.fn(),
  signUp: vi.fn(),
  verifyMfa: vi.fn(),
}))

const tokenFor = (claims: Record<string, string> = {}) =>
  "header." + btoa(JSON.stringify(claims)).replace(/=/g, "").replace(/\+/g, "-").replace(/\//g, "_") + ".signature"

const grants = [{
  permissionId: 1,
  name: "Role Management",
  path: "/auth/role",
  actions: [{ actionId: 1, code: "view", name: "View" }],
}]

let auth: ReturnType<typeof useAuthStore>

beforeEach(() => {
  localStorage.clear()
  sessionStorage.clear()
  vi.resetAllMocks()
  setActivePinia(createPinia())
  auth = useAuthStore()
})

afterEach(() => auth.$dispose())

describe("Pinia auth session", () => {
  it("hydrates the existing session and its canonical permission grants", () => {
    persistAuthSession({
      accessToken: tokenFor({ password_change_required: "true" }),
      refreshToken: "refresh",
      userName: "Operator",
      email: "operator@example.com",
      permissions: grants,
    })
    expect(auth.isAuthenticated).toBe(true)
    expect(auth.claims?.password_change_required).toBe("true")
    expect(auth.userName).toBe("Operator")
    expect(auth.userEmail).toBe("operator@example.com")
    expect(auth.permissions).toEqual(grants)
  })

  it.each(["not-a-jwt", "header.%%%.signature", "header.bnVsbA.signature"])(
    "fails closed for malformed credential %s", (token) => {
      persistAuthSession({ accessToken: token, permissions: grants })
      expect(auth.isAuthenticated).toBe(false)
      expect(auth.claims).toBeNull()
    },
  )

  it.each(["not-json", "{}", "[{}]", '[{"path":"/auth/role","actions":[{}]}]'])(
    "does not interpret invalid permission data as grants", (raw) => {
      localStorage.setItem("accessToken", tokenFor())
      localStorage.setItem("permissions", raw)
      auth.hydrate()
      expect(auth.isAuthenticated).toBe(true)
      expect(auth.permissions).toEqual([])
    },
  )

  it("centralizes sign-in and immediately updates its reactive identity and grants", async () => {
    vi.mocked(signIn).mockResolvedValue({
      accessToken: tokenFor(),
      refreshToken: "refresh",
      userName: "Test User",
      email: "test@example.com",
      permissions: grants,
    })
    expect(await auth.login("  test@example.com  ", "secret")).toBe("authenticated")
    expect(signIn).toHaveBeenCalledWith("test@example.com", "secret")
    expect(auth.isAuthenticated).toBe(true)
    expect(auth.permissions).toEqual(grants)
    expect(auth.userName).toBe("Test User")
    expect(localStorage.getItem("refreshToken")).toBe("refresh")
  })

  it("does not accept a partial login or registration response as authenticated", async () => {
    vi.mocked(signIn).mockResolvedValue({ accessToken: tokenFor() })
    vi.mocked(signUp).mockResolvedValue({ accessToken: tokenFor() })
    expect(await auth.login("user@example.com", "secret")).toBe("invalid")
    expect(await auth.register(" user@example.com ", "secret")).toBe(false)
    expect(signUp).toHaveBeenCalledWith("user@example.com", "secret")
    expect(auth.isAuthenticated).toBe(false)
    expect(localStorage.getItem("accessToken")).toBeNull()
  })

  it("never carries previous-account grants into a different successful sign-in", async () => {
    persistAuthSession({
      accessToken: tokenFor(), refreshToken: "old-refresh",
      userName: "Old user", email: "old@example.com", permissions: grants,
    })
    vi.mocked(signIn).mockResolvedValue({
      accessToken: tokenFor(), refreshToken: "new-refresh",
    })
    expect(await auth.login("new@example.com", "secret")).toBe("authenticated")
    expect(auth.permissions).toEqual([])
    expect(auth.userName).toBe("")
    expect(auth.userEmail).toBe("")
    expect(localStorage.getItem("permissions")).toBeNull()
  })

  it("re-hydrates from another tab's storage change and cleans up event listeners", () => {
    persistAuthSession({
      accessToken: tokenFor(), refreshToken: "refresh", permissions: grants,
    })
    localStorage.removeItem("accessToken")
    localStorage.removeItem("permissions")
    window.dispatchEvent(new StorageEvent("storage", {
      key: "accessToken",
      storageArea: localStorage,
    }))
    expect(auth.isAuthenticated).toBe(false)
    expect(auth.permissions).toEqual([])
  })

  it("keeps the MFA challenge separate from login credentials until verified", async () => {
    vi.mocked(signIn).mockResolvedValue({ requiresMfa: true, mfaToken: "challenge" })
    expect(await auth.login("person@example.com", "secret")).toBe("mfa")
    expect(auth.isAuthenticated).toBe(false)
    expect(sessionStorage.getItem("mfaToken")).toBe("challenge")

    vi.mocked(verifyMfa).mockResolvedValue({
      accessToken: tokenFor({ mfa_enrollment_required: "true" }),
      refreshToken: "refreshed",
    })
    await auth.completeMfa("123456")
    expect(verifyMfa).toHaveBeenCalledWith("challenge", "123456")
    expect(auth.isAuthenticated).toBe(true)
    expect(auth.claims?.mfa_enrollment_required).toBe("true")
    expect(sessionStorage.getItem("mfaToken")).toBeNull()
  })

  it("keeps a failed MFA challenge available for retry", async () => {
    sessionStorage.setItem("mfaToken", "retry-challenge")
    vi.mocked(verifyMfa).mockRejectedValue(new Error("bad code"))
    await expect(auth.completeMfa("000000")).rejects.toThrow("bad code")
    expect(sessionStorage.getItem("mfaToken")).toBe("retry-challenge")
    expect(auth.isAuthenticated).toBe(false)
  })

  it("runs SSO through the same MFA and token persistence boundary", async () => {
    vi.mocked(exchangeOidcCode).mockResolvedValueOnce({
      requiresMfa: true, mfaToken: "sso-mfa",
    }).mockResolvedValueOnce({
      accessToken: tokenFor(), refreshToken: "sso-refresh",
    })
    expect(await auth.completeSso("one-time-code")).toBe("mfa")
    expect(sessionStorage.getItem("mfaToken")).toBe("sso-mfa")
    expect(await auth.completeSso("another-code")).toBe("authenticated")
    expect(sessionStorage.getItem("mfaToken")).toBeNull()
    expect(auth.isAuthenticated).toBe(true)
  })

  it("updates grants after HTTP refresh and clears all reactive state after revocation", () => {
    persistAuthSession({
      accessToken: tokenFor(), refreshToken: "old", permissions: grants,
    })
    persistAuthSession({
      accessToken: tokenFor(), refreshToken: "new", permissions: [],
    })
    expect(auth.permissions).toEqual([])
    expect(localStorage.getItem("refreshToken")).toBe("new")
    clearAuthSession()
    expect(auth.isAuthenticated).toBe(false)
    expect(auth.permissions).toEqual([])
    expect(auth.accessToken).toBeNull()
  })

  it("clears session and MFA even if the sign-out request fails", async () => {
    persistAuthSession({
      accessToken: tokenFor(), refreshToken: "refresh", permissions: grants,
    })
    sessionStorage.setItem("mfaToken", "stale")
    vi.mocked(signOut).mockRejectedValue(new Error("network unavailable"))
    await expect(auth.logout()).rejects.toThrow("network unavailable")
    expect(auth.isAuthenticated).toBe(false)
    expect(auth.permissions).toEqual([])
    expect(sessionStorage.getItem("mfaToken")).toBeNull()
    expect(localStorage.length).toBe(0)
  })
})
