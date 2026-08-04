export default defineNuxtRouteMiddleware((to, _from) => {
  if (import.meta.server) return;
  const token = window.localStorage.getItem("accessToken");
  const isLogin = !!token;
  const publicRoutes = ["/login", "/sign-up", "/forgot-password", "/reset-password", "/403"];
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
  } catch {
    return navigateTo("/login");
  }

  const required = to.meta.permission as string | undefined;
  if (required) {
    const dot = required.lastIndexOf(".");
    if (dot === -1) return navigateTo("/403");
    const path = required.slice(0, dot);
    const action = required.slice(dot + 1);
    try {
      const raw = localStorage.getItem("permissions");
      const perms: any[] = raw ? JSON.parse(raw) : [];
      const ok = perms.some((p: any) => p.path === path && p.actions.some((a: any) => a.code === action));
      if (!ok) return navigateTo("/403");
    } catch {
      return navigateTo("/403");
    }
  }
});
