import { checkStoredPermission } from "./permissions"

const PUBLIC_ROUTES = new Set([
  "/login",
  "/sign-up",
  "/forgot-password",
  "/reset-password",
  "/sso-callback",
  "/mfa",
  "/403",
])

interface JwtPayload {
  password_change_required?: unknown
  mfa_enrollment_required?: unknown
}

export interface AuthRouteDecisionInput {
  path: string
  requiredPermission?: string
  token: string | null
  rawPermissions: string | null
}

function decodeJwtPayload(token: string): JwtPayload | null {
  const payloadPart = token.split(".")[1]
  if (!payloadPart) return null

  const normalized = payloadPart.replace(/-/g, "+").replace(/_/g, "/")
  const padded = normalized.padEnd(
    normalized.length + ((4 - (normalized.length % 4)) % 4),
    "=",
  )

  return JSON.parse(atob(padded)) as JwtPayload
}

export function resolveAuthRedirect({
  path,
  requiredPermission,
  token,
  rawPermissions,
}: AuthRouteDecisionInput): string | null {
  if (!token) {
    return PUBLIC_ROUTES.has(path) ? null : "/login"
  }

  if (path === "/") return "/home"
  if (PUBLIC_ROUTES.has(path)) return null

  let payload: JwtPayload | null
  try {
    payload = decodeJwtPayload(token)
  } catch {
    return "/login"
  }

  if (
    payload?.password_change_required === "true" &&
    path !== "/account"
  ) {
    return "/account"
  }

  if (
    payload?.mfa_enrollment_required === "true" &&
    path !== "/account"
  ) {
    return "/account"
  }

  if (
    requiredPermission &&
    !checkStoredPermission(requiredPermission, path, rawPermissions)
  ) {
    return "/403"
  }

  return null
}
