import { watch } from "vue"
import { useAuthStore } from "@/stores/auth"
import { canAccess, type PermissionGrant } from "@/lib/permissions"

 export default defineNuxtPlugin((nuxtApp) => {
  const route = useRoute()
  const auth = useAuthStore()
  const directiveWatchers = new WeakMap<HTMLElement, () => void>()

  function currentCheck(value: string | string[]) {
    // Pinia is the reactive source of permission grants. API authorization
    // remains enforced by the backend regardless of this UI hint.

    // Relative action names are resolved against the page's declared canonical permission,
    // never against its navigational URL. A route can therefore move without changing its
    // authorization identity.
    const pagePermission = typeof route.meta.permission === "string"
      ? route.meta.permission
      : undefined
    return canAccess(auth.permissions, value, pagePermission)
  }

  nuxtApp.vueApp.directive("permission", {
    mounted(el, binding) {
      const applyVisibility = () => {
        el.style.display = currentCheck(binding.value) ? "" : "none"
      }
      applyVisibility()
      directiveWatchers.set(el, watch(
        [() => auth.permissions, () => route.meta.permission],
        applyVisibility,
      ))
    },
    updated(el, binding) {
      el.style.display = currentCheck(binding.value) ? "" : "none"
    },
    unmounted(el) {
      directiveWatchers.get(el)?.()
      directiveWatchers.delete(el)
    },
  })

  return {
    provide: {
      can: (value: string | string[]) => currentCheck(value),
    },
  }
})
