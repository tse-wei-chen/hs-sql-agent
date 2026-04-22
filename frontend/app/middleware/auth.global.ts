export default defineNuxtRouteMiddleware((to, from) => {
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
});
