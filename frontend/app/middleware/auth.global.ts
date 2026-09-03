import { resolveAuthRedirect } from "@/lib/auth-route"

export default defineNuxtRouteMiddleware((to, _from) => {
  if (import.meta.server) return

  const storage = window.localStorage
  const redirect = resolveAuthRedirect({
    path: to.path,
    requiredPermission: to.meta.permission as string | undefined,
    token: storage.getItem("accessToken"),
    rawPermissions: storage.getItem("permissions"),
  })

  return redirect ? navigateTo(redirect) : undefined
})
