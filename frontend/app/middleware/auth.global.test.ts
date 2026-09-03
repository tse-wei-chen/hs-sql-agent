import { describe, expect, it } from "vitest"
import { resolveAuthRedirect } from "@/lib/auth-route"

function tokenWithPayload(payload: Record<string, unknown>): string {
  const encoded = btoa(JSON.stringify(payload))
    .replace(/=/g, "")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")

  return `header.${encoded}.signature`
}

const allowedPermissions = JSON.stringify([
  {
    path: "/runtime/security",
    actions: [{ code: "view" }, { code: "edit" }],
  },
])

describe("auth route decision", () => {
  it("redirects unauthenticated users away from protected routes", () => {
    expect(
      resolveAuthRedirect({
        path: "/home",
        token: null,
        rawPermissions: null,
      }),
    ).toBe("/login")
  })

  it.each([
    "/login",
    "/sign-up",
    "/forgot-password",
    "/reset-password",
    "/sso-callback",
    "/mfa",
    "/403",
  ])("allows unauthenticated access to %s", (path) => {
    expect(
      resolveAuthRedirect({
        path,
        token: null,
        rawPermissions: null,
      }),
    ).toBeNull()
  })

  it("redirects an authenticated root visit to home", () => {
    expect(
      resolveAuthRedirect({
        path: "/",
        token: "opaque-token",
        rawPermissions: null,
      }),
    ).toBe("/home")
  })

  it("keeps authenticated public routes accessible", () => {
    expect(
      resolveAuthRedirect({
        path: "/login",
        token: "opaque-token",
        rawPermissions: null,
      }),
    ).toBeNull()
  })

  it("forces password-change sessions to the account page", () => {
    const token = tokenWithPayload({ password_change_required: "true" })

    expect(
      resolveAuthRedirect({
        path: "/runtime/security",
        token,
        rawPermissions: allowedPermissions,
      }),
    ).toBe("/account")

    expect(
      resolveAuthRedirect({
        path: "/account",
        token,
        rawPermissions: allowedPermissions,
      }),
    ).toBeNull()
  })

  it("forces MFA-enrollment sessions to the account page", () => {
    const token = tokenWithPayload({ mfa_enrollment_required: "true" })

    expect(
      resolveAuthRedirect({
        path: "/runtime/security",
        token,
        rawPermissions: allowedPermissions,
      }),
    ).toBe("/account")
  })

  it("allows routes when the required permission is present", () => {
    expect(
      resolveAuthRedirect({
        path: "/runtime/security",
        requiredPermission: "/runtime/security.view",
        token: tokenWithPayload({}),
        rawPermissions: allowedPermissions,
      }),
    ).toBeNull()
  })

  it("fails closed when the required permission is absent or malformed", () => {
    expect(
      resolveAuthRedirect({
        path: "/runtime/security",
        requiredPermission: "/runtime/security.delete",
        token: tokenWithPayload({}),
        rawPermissions: allowedPermissions,
      }),
    ).toBe("/403")

    expect(
      resolveAuthRedirect({
        path: "/runtime/security",
        requiredPermission: "/runtime/security.view",
        token: tokenWithPayload({}),
        rawPermissions: "{not-json",
      }),
    ).toBe("/403")
  })

  it("redirects malformed JWT payloads to login", () => {
    expect(
      resolveAuthRedirect({
        path: "/home",
        token: "header.%%%invalid%%%.signature",
        rawPermissions: null,
      }),
    ).toBe("/login")
  })
})
