import { createPinia } from "pinia"
import { useAuthStore } from "@/stores/auth"

// Nuxt 4 uses client rendering in this Admin UI. Install Pinia before the
// permission plugin and route middleware, then hydrate its storage projection.
export default defineNuxtPlugin({
  name: "auth-pinia",
  enforce: "pre",
  setup(nuxtApp) {
    const pinia = createPinia()
    nuxtApp.vueApp.use(pinia)
    useAuthStore(pinia).hydrate()
  },
})
