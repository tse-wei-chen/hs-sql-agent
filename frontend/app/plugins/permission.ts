interface ActionGrant {
  actionId: number
  code: string
  name: string
}

interface PermissionGrant {
  permissionId: number
  name: string
  path: string
  actions: ActionGrant[]
}

function getPermissions(): PermissionGrant[] {
  try {
    const raw = localStorage.getItem("permissions")
    return raw ? JSON.parse(raw) : []
  } catch {
    return []
  }
}

function hasPermission(path: string, action: string): boolean {
  return getPermissions().some(p => p.path === path && p.actions.some(a => a.code === action))
}

function resolveAction(value: string, currentPath: string) {
  return value.startsWith("/") ? value : `${currentPath}.${value}`
}

function checkValue(value: string | string[], currentPath: string): boolean {
  if (typeof value === "string") {
    const resolved = resolveAction(value, currentPath)
    const dot = resolved.lastIndexOf(".")
    if (dot === -1) return false
    return hasPermission(resolved.slice(0, dot), resolved.slice(dot + 1))
  }
  if (Array.isArray(value)) {
    return value.some((v) => checkValue(v, currentPath))
  }
  return false
}

export default defineNuxtPlugin((nuxtApp) => {
  const route = useRoute()

  function currentCheck(value: string | string[]) {
    return checkValue(value, route.path)
  }

  nuxtApp.vueApp.directive("permission", {
    mounted(el, binding) {
      if (!currentCheck(binding.value)) {
        el.style.display = "none"
      }
    },
    updated(el, binding) {
      if (!currentCheck(binding.value)) {
        el.style.display = "none"
      } else {
        el.style.display = ""
      }
    },
  })

  return {
    provide: {
      can: (value: string | string[]) => currentCheck(value),
    },
  }
})
