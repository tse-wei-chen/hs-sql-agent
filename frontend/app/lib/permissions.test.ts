import { describe, expect, it } from "vitest"
import {
  Permissions,
  canAccess,
  hasPermissionKey,
  parsePermissionKey,
  resolvePermissionKey,
  type PermissionGrant,
} from "./permissions"

const grants: PermissionGrant[] = [
  {
    permissionId: 1,
    name: "Role Management",
    path: "/auth/role",
    actions: [
      { actionId: 1, code: "view", name: "view" },
      { actionId: 2, code: "edit", name: "edit" },
    ],
  },
]

describe("canonical permission helpers", () => {
  it("parses only canonical permission keys", () => {
    expect(parsePermissionKey(Permissions.Auth.Role.View)).toEqual({
      path: "/auth/role",
      action: "view",
    })
    expect(parsePermissionKey("edit")).toBeNull()
    expect(parsePermissionKey("auth/role.view")).toBeNull()
  })

  it("resolves relative actions against page canonical metadata, not a route URL", () => {
    expect(resolvePermissionKey("edit", Permissions.Auth.Role.View)).toBe(Permissions.Auth.Role.Edit)
    expect(resolvePermissionKey("edit", "/admin/roles.view")).toBe("/admin/roles.edit")
    expect(resolvePermissionKey("edit")).toBeNull()
  })

  it("checks grants using canonical path and action", () => {
    expect(hasPermissionKey(grants, Permissions.Auth.Role.View)).toBe(true)
    expect(hasPermissionKey(grants, Permissions.Auth.Role.Edit)).toBe(true)
    expect(hasPermissionKey(grants, Permissions.Auth.Role.Delete)).toBe(false)
  })

  it("supports relative directives only when page metadata declares a canonical permission", () => {
    expect(canAccess(grants, "edit", Permissions.Auth.Role.View)).toBe(true)
    expect(canAccess(grants, "edit")).toBe(false)
    expect(canAccess(grants, ["delete", "edit"], Permissions.Auth.Role.View)).toBe(true)
  })
})
