export default defineNuxtRouteMiddleware((to, _from) => {
  if (import.meta.server) return;
  const token = window.localStorage.getItem("accessToken");
  const isLogin = !!token;
  const authRoutes = ["/login", "/sign-up"];
  if (!isLogin && !authRoutes.includes(to.path)) {
    return navigateTo("/login");
  }
  if (isLogin && [...authRoutes, "/"].includes(to.path)) {
    return navigateTo("/home");
  }

  const required = to.meta.permission as string | undefined;
  if (required) {
    const dot = required.lastIndexOf(".");
    if (dot === -1) return navigateTo("/home");
    const path = required.slice(0, dot);
    const action = required.slice(dot + 1);
    try {
      const raw = localStorage.getItem("permissions");
      const perms: any[] = raw ? JSON.parse(raw) : [];
      const ok = perms.some((p: any) => p.path === path && p.actions.some((a: any) => a.code === action));
      if (!ok) return navigateTo("/home");
    } catch {
      return navigateTo("/home");
    }
  }
});
