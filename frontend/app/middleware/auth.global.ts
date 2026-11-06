import { hasPermissionKey, type PermissionGrant } from "@/lib/permissions"

export default defineNuxtRouteMiddleware((to, _from) => {
  if (import.meta.server) return;

  const publicRoutes = ["/login", "/sign-up", "/forgot-password", "/reset-password", "/sso-callback", "/mfa", "/403"];
  const token = window.localStorage.getItem("accessToken");

  if (!token) {
    if (!publicRoutes.includes(to.path)) return navigateTo("/login");
    return;
  }

  // Only use locally decoded claims to guide navigation. The server is still
  // responsible for authenticating and authorizing every API request.
  let claims: Record<string, unknown>;
  try {
    const segments = token.split(".");
    if (segments.length !== 3 || !segments[1]) throw new Error("Malformed token");
    const parsed: unknown = JSON.parse(atob(segments[1].replace(/-/g, "+").replace(/_/g, "/")));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      throw new Error("Malformed token payload");
    }
    claims = parsed as Record<string, unknown>;
  } catch {
    // Keep recovery/authentication routes reachable even if stale local
    // credentials cannot be decoded; never bounce /login back to itself.
    if (publicRoutes.includes(to.path)) return;
    return navigateTo("/login");
  }

  if (to.path === "/" || to.path === "/login" || to.path === "/sign-up") {
    return navigateTo("/home");
  }
  if (publicRoutes.includes(to.path)) return;

  if (claims.password_change_required === "true" && to.path !== "/account") {
    return navigateTo("/account");
  }
  if (claims.mfa_enrollment_required === "true" && to.path !== "/account") {
    return navigateTo("/account");
  }

  const required = to.meta.permission as string | undefined;
  if (required) {
    try {
      const raw = localStorage.getItem("permissions");
      const grants: PermissionGrant[] = raw ? JSON.parse(raw) : [];
      if (!hasPermissionKey(grants, required)) return navigateTo("/403");
    } catch {
      return navigateTo("/403");
    }
  }
});
