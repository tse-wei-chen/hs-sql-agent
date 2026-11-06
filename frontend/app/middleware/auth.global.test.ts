import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import type { PermissionGrant } from "@/lib/permissions";

// Run the production Nuxt middleware, not a second copy of its routing rules.
type TestRoute = { path: string; meta: { permission?: string } };
type RouteMiddleware = (to: TestRoute, from: TestRoute) => unknown;
const navigate = vi.fn((path: string) => path);
let middleware: RouteMiddleware;

const route = (path: string, permission?: string): TestRoute => ({
  path,
  meta: permission ? { permission } : {},
});

const tokenFor = (claims: Record<string, string> = {}) =>
  "header." + btoa(JSON.stringify(claims)).replace(/=/g, "").replace(/\+/g, "-").replace(/\//g, "_") + ".signature";

function visit(path: string, permission?: string) {
  return middleware(route(path, permission), route("/previous"));
}

const roleViewGrant: PermissionGrant[] = [{
  permissionId: 1,
  name: "Role Management",
  path: "/auth/role",
  actions: [{ actionId: 1, code: "view", name: "View" }],
}];

beforeAll(async () => {
  // Vitest does not run Nuxt auto-import transforms: supply only the two
  // global Nuxt entry points required to execute the real middleware.
  vi.stubGlobal("defineNuxtRouteMiddleware", (handler: RouteMiddleware) => handler);
  vi.stubGlobal("navigateTo", navigate);
  middleware = (await import("./auth.global")).default as unknown as RouteMiddleware;
});

beforeEach(() => {
  window.localStorage.clear();
  navigate.mockClear();
});

afterAll(() => vi.unstubAllGlobals());

describe("production auth.global middleware", () => {
  it.each(["/home", "/runtime/custom-tools", "/runtime/db-management"])(
    "sends an anonymous request for %s to login",
    (path) => {
      expect(visit(path)).toBe("/login");
      expect(navigate).toHaveBeenCalledExactlyOnceWith("/login");
    },
  );

  it.each(["/login", "/sign-up", "/forgot-password", "/reset-password", "/sso-callback", "/mfa", "/403"])(
    "allows the public route %s without a token",
    (path) => {
      expect(visit(path)).toBeUndefined();
      expect(navigate).not.toHaveBeenCalled();
    },
  );

  it.each(["/", "/login", "/sign-up"])(
    "sends an authenticated visitor on %s to home",
    (path) => {
      window.localStorage.setItem("accessToken", tokenFor());
      expect(visit(path)).toBe("/home");
      expect(navigate).toHaveBeenCalledExactlyOnceWith("/home");
    },
  );

  it.each(["/mfa", "/sso-callback", "/reset-password", "/403"])(
    "keeps %s available during authentication workflows",
    (path) => {
      window.localStorage.setItem("accessToken", tokenFor());
      expect(visit(path)).toBeUndefined();
      expect(navigate).not.toHaveBeenCalled();
    },
  );

  it("allows an authenticated request to an unrestricted route", () => {
    window.localStorage.setItem("accessToken", tokenFor());
    expect(visit("/runtime/audit")).toBeUndefined();
    expect(navigate).not.toHaveBeenCalled();
  });

  it.each(["password_change_required", "mfa_enrollment_required"])(
    "enforces %s until account setup is complete",
    (claim) => {
      window.localStorage.setItem("accessToken", tokenFor({ [claim]: "true" }));
      expect(visit("/home")).toBe("/account");
      expect(navigate).toHaveBeenCalledExactlyOnceWith("/account");
      navigate.mockClear();
      expect(visit("/account")).toBeUndefined();
      expect(navigate).not.toHaveBeenCalled();
    },
  );

  it.each(["not-a-jwt", "header.invalid-%%%.signature", "header.bnVsbA.signature"])(
    "rejects malformed credentials instead of treating %s as authenticated",
    (invalid) => {
      window.localStorage.setItem("accessToken", invalid);
      expect(visit("/runtime/audit")).toBe("/login");
      expect(navigate).toHaveBeenCalledExactlyOnceWith("/login");
    },
  );

  it("allows the exact canonical permission granted to the user", () => {
    window.localStorage.setItem("accessToken", tokenFor());
    window.localStorage.setItem("permissions", JSON.stringify(roleViewGrant));
    expect(visit("/auth/role", "/auth/role.view")).toBeUndefined();
    expect(navigate).not.toHaveBeenCalled();
  });

  it.each([
    [JSON.stringify(roleViewGrant), "/auth/role.delete"],
    [JSON.stringify(roleViewGrant), "/auth/user.view"],
    ["[]", "/auth/role.view"],
    ["not-json", "/auth/role.view"],
  ])("denies missing, mismatched or malformed permission data", (grants, required) => {
    window.localStorage.setItem("accessToken", tokenFor());
    window.localStorage.setItem("permissions", grants);
    expect(visit("/auth/role", required)).toBe("/403");
    expect(navigate).toHaveBeenCalledExactlyOnceWith("/403");
  });
});
