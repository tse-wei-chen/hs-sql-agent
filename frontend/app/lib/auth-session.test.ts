import { beforeEach, describe, expect, it } from "vitest"
import {
  authSessionRevision,
  clearAuthSession,
  persistAuthSession,
} from "./auth-session"

describe("auth session storage", () => {
  beforeEach(() => localStorage.clear())

  it("updates permissions and identity when a refreshed auth result includes them", () => {
    persistAuthSession({
      accessToken: "access-2",
      refreshToken: "refresh-2",
      permissions: [{ path: "/home", actions: [{ code: "view" }] }],
      email: "operator@example.com",
      userName: "operator",
    })

    expect(localStorage.getItem("accessToken")).toBe("access-2")
    expect(localStorage.getItem("refreshToken")).toBe("refresh-2")
    expect(JSON.parse(localStorage.getItem("permissions")!)).toEqual([
      { path: "/home", actions: [{ code: "view" }] },
    ])
    expect(localStorage.getItem("userEmail")).toBe("operator@example.com")
    expect(localStorage.getItem("userName")).toBe("operator")
  })

  it("does not erase existing optional values when a partial response omits them", () => {
    localStorage.setItem("permissions", "[{\"path\":\"/home\"}]")
    localStorage.setItem("userName", "existing")

    persistAuthSession({ accessToken: "access-2", refreshToken: "refresh-2" })

    expect(localStorage.getItem("permissions")).toBe("[{\"path\":\"/home\"}]")
    expect(localStorage.getItem("userName")).toBe("existing")
  })

  it("clears every locally stored authentication artifact", () => {
    persistAuthSession({
      accessToken: "access",
      refreshToken: "refresh",
      permissions: [],
      email: "operator@example.com",
      userName: "operator",
    })

    clearAuthSession()

    expect(localStorage.length).toBe(0)
  })

  it("notifies reactive permission consumers after persistence and clearing", () => {
    const initialRevision = authSessionRevision.value

    persistAuthSession({ permissions: [] })
    expect(authSessionRevision.value).toBe(initialRevision + 1)

    clearAuthSession()
    expect(authSessionRevision.value).toBe(initialRevision + 2)
  })
})
