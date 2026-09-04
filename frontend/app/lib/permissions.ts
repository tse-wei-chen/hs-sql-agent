export interface ActionGrant {
  actionId: number
  code: string
  name: string
}

export interface PermissionGrant {
  permissionId: number
  name: string
  path: string
  actions: ActionGrant[]
}

export const Permissions = {
  Home: {
    View: "/home.view",
  },
  Auth: {
    Role: {
      View: "/auth/role.view",
      Create: "/auth/role.create",
      Edit: "/auth/role.edit",
      Delete: "/auth/role.delete",
    },
    User: {
      View: "/auth/user.view",
      Create: "/auth/user.create",
      Edit: "/auth/user.edit",
      Delete: "/auth/user.delete",
    },
  },
  Runtime: {
    McpKeys: {
      View: "/runtime/mcp-keys.view",
      Create: "/runtime/mcp-keys.create",
      Edit: "/runtime/mcp-keys.edit",
      Revoke: "/runtime/mcp-keys.revoke",
    },
    CustomTools: {
      View: "/runtime/custom-tools.view",
      Create: "/runtime/custom-tools.create",
      Edit: "/runtime/custom-tools.edit",
      Delete: "/runtime/custom-tools.delete",
    },
    DbManagement: {
      View: "/runtime/db-management.view",
      Create: "/runtime/db-management.create",
      Edit: "/runtime/db-management.edit",
      Delete: "/runtime/db-management.delete",
      Semantic: {
        View: "/runtime/db-management/semantic.view",
        Edit: "/runtime/db-management/semantic.edit",
      },
    },
    Audit: {
      View: "/runtime/audit.view",
      Edit: "/runtime/audit.edit",
      Export: "/runtime/audit.export",
    },
    Security: {
      View: "/runtime/security.view",
      Edit: "/runtime/security.edit",
    },
    Operability: {
      View: "/runtime/operability.view",
      Edit: "/runtime/operability.edit",
    },
  },
} as const

export interface ParsedPermissionKey {
  path: string
  action: string
}

export function parsePermissionKey(value: string | undefined | null): ParsedPermissionKey | null {
  if (!value?.startsWith("/")) return null
  const dot = value.lastIndexOf(".")
  if (dot <= 0 || dot === value.length - 1) return null
  return {
    path: value.slice(0, dot),
    action: value.slice(dot + 1),
  }
}

export function resolvePermissionKey(value: string, pagePermission?: string): string | null {
  if (value.startsWith("/")) return parsePermissionKey(value) ? value : null

  const page = parsePermissionKey(pagePermission)
  if (!page || value.includes(".")) return null
  return `${page.path}.${value}`
}

export function hasPermissionKey(grants: PermissionGrant[], permissionKey: string): boolean {
  const parsed = parsePermissionKey(permissionKey)
  if (!parsed) return false
  return grants.some(
    grant => grant.path === parsed.path && grant.actions.some(action => action.code === parsed.action),
  )
}

export function canAccess(
  grants: PermissionGrant[],
  value: string | string[],
  pagePermission?: string,
): boolean {
  const values = Array.isArray(value) ? value : [value]
  return values.some((candidate) => {
    const resolved = resolvePermissionKey(candidate, pagePermission)
    return resolved ? hasPermissionKey(grants, resolved) : false
  })
}
