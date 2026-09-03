import { describe, expect, it } from "vitest"
import {
  checkPermissionValue,
  checkStoredPermission,
  parsePermissionGrants,
} from "./permissions"

const permissions = [
  {
    path: "/runtime/security",
    actions: [{ code: "view" }, { code: "edit" }],
  },
  {
    path: "/runtime/audit",
    actions: [{ code: "export" }],
  },
]

describe("permission helpers", () => {
  it("checks absolute and route-relative permission values", () => {
    expect(
      checkPermissionValue(
        "/runtime/security.view",
        "/ignored",
        permissions,
      ),
    ).toBe(true)

    expect(
      checkPermissionValue("edit", "/runtime/security", permissions),
    ).toBe(true)
  })

  it("uses any-of semantics for permission arrays", () => {
    expect(
      checkPermissionValue(
        ["/runtime/security.delete", "/runtime/audit.export"],
        "/home",
        permissions,
      ),
    ).toBe(true)

    expect(
      checkPermissionValue(
        ["/runtime/security.delete", "/runtime/audit.edit"],
        "/home",
        permissions,
      ),
    ).toBe(false)
  })

  it("fails closed for malformed permission expressions", () => {
    expect(
      checkPermissionValue("/runtime/security", "/home", permissions),
    ).toBe(false)
  })

  it("fails closed for malformed or unexpected stored permission JSON", () => {
    expect(checkStoredPermission("view", "/runtime/security", "{bad-json")).toBe(
      false,
    )
    expect(checkStoredPermission("view", "/runtime/security", "{}")).toBe(false)
  })

  it("ignores malformed grants instead of throwing", () => {
    const parsed = parsePermissionGrants(
      JSON.stringify([
        null,
        { path: "/runtime/security", actions: "view" },
        { path: "/runtime/security", actions: [{ code: "view" }] },
      ]),
    )

    expect(parsed).toEqual([
      { path: "/runtime/security", actions: [{ code: "view" }] },
    ])
    expect(
      checkPermissionValue("view", "/runtime/security", parsed),
    ).toBe(true)
  })
})
