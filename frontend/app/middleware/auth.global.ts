import { hasPermissionKey, type PermissionGrant } from "@/lib/permissions"

export default defineNuxtRouteMiddleware((to, _from) => {
  if (import.meta.server) return;
  const token = window.localStorage.getItem("accessToken");
  const isLogin = !!token;
  const publicRoutes = ["/login", "/sign-up", "/forgot-password", "/reset-password", "/sso-callback", "/mfa", "/403"];
  if (!isLogin && !publicRoutes.includes(to.path)) {
    return navigateTo("/login");
  }
  if (isLogin && to.path === "/") {
    return navigateTo("/home");
  }
  if (isLogin && publicRoutes.includes(to.path)) {
    return;
  }

  try {
    const payloadPart = token?.split(".")[1];
    const payload = payloadPart ? JSON.parse(atob(payloadPart.replace(/-/g, "+").replace(/_/g, "/"))) : null;
    if (payload?.password_change_required === "true" && to.path !== "/account") {
      return navigateTo("/account");
    }
    if (payload?.mfa_enrollment_required === "true" && to.path !== "/account") {
      return navigateTo("/account");
    }
  } catch {
    return navigateTo("/login");
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
