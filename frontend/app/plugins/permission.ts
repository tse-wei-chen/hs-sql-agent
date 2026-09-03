import { watch } from "vue"
import { authSessionRevision } from "@/lib/auth-session"
import { checkStoredPermission } from "@/lib/permissions"

export default defineNuxtPlugin((nuxtApp) => {
  const route = useRoute()
  const directiveWatchers = new WeakMap<HTMLElement, () => void>()

  function currentCheck(value: string | string[]) {
    // Establish a reactive dependency so computed callers of $can update after
    // token refresh, sign-in, or sign-out changes the stored permission grants.
    void authSessionRevision.value
    return checkStoredPermission(
      value,
      route.path,
      localStorage.getItem("permissions"),
    )
  }

  nuxtApp.vueApp.directive("permission", {
    mounted(el, binding) {
      const applyVisibility = () => {
        el.style.display = currentCheck(binding.value) ? "" : "none"
      }
      applyVisibility()
      directiveWatchers.set(el, watch(authSessionRevision, applyVisibility))
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
