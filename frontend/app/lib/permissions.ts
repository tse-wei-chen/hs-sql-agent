export interface ActionGrant {
  code: string
}

export interface PermissionGrant {
  path: string
  actions: ActionGrant[]
}

export function parsePermissionGrants(rawPermissions: string | null): PermissionGrant[] {
  if (!rawPermissions) return []

  try {
    const parsed: unknown = JSON.parse(rawPermissions)
    if (!Array.isArray(parsed)) return []

    return parsed.filter((grant): grant is PermissionGrant => {
      if (!grant || typeof grant !== "object") return false

      const candidate = grant as {
        path?: unknown
        actions?: unknown
      }

      return typeof candidate.path === "string" && Array.isArray(candidate.actions)
    })
  } catch {
    return []
  }
}

export function hasPermission(
  permissions: PermissionGrant[],
  path: string,
  action: string,
): boolean {
  return permissions.some(
    (permission) =>
      permission.path === path &&
      permission.actions.some(
        (grant) => grant && typeof grant.code === "string" && grant.code === action,
      ),
  )
}

export function resolvePermissionAction(value: string, currentPath: string): string {
  return value.startsWith("/") ? value : `${currentPath}.${value}`
}

export function checkPermissionValue(
  value: string | string[],
  currentPath: string,
  permissions: PermissionGrant[],
): boolean {
  if (Array.isArray(value)) {
    return value.some((candidate) =>
      checkPermissionValue(candidate, currentPath, permissions),
    )
  }

  const resolved = resolvePermissionAction(value, currentPath)
  const separator = resolved.lastIndexOf(".")
  if (separator === -1) return false

  return hasPermission(
    permissions,
    resolved.slice(0, separator),
    resolved.slice(separator + 1),
  )
}

export function checkStoredPermission(
  value: string | string[],
  currentPath: string,
  rawPermissions: string | null,
): boolean {
  return checkPermissionValue(
    value,
    currentPath,
    parsePermissionGrants(rawPermissions),
  )
}
