import { hasPermissionKey } from "@/lib/permissions"
import { useAuthStore } from "@/stores/auth"

export default defineNuxtRouteMiddleware((to, _from) => {
  if (import.meta.server) return;

  const publicRoutes = ["/login", "/sign-up", "/forgot-password", "/reset-password", "/sso-callback", "/mfa", "/403"];
  const auth = useAuthStore();
  auth.hydrate();
  const token = auth.accessToken;

  if (!token) {
    if (!publicRoutes.includes(to.path)) return navigateTo("/login");
    return;
  }

  // Only use locally decoded claims to guide navigation. The server is still
  // responsible for authenticating and authorizing every API request.
  const claims = auth.claims;
  if (!claims) {
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
  if (required && !hasPermissionKey(auth.permissions, required)) {
    return navigateTo("/403");
  }
});
