import tailwindcss from "@tailwindcss/vite";

// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  ssr: false,

  compatibilityDate: "2025-07-15",
  devtools: { enabled: true },
  srcDir: "app/",

  css: ["~/assets/css/tailwind.css"],

  // 2.
  nitro: {
    output: {
      publicDir: "dist",
    },
  },

  // 3. dev mode
  routeRules: {
    "/api/**": {
      proxy: "http://localhost:8080/api/**",
    },
  },

  vite: {
    plugins: [tailwindcss()],
    optimizeDeps: {
      include: [
        "@vueuse/core",
        "@unovis/vue",
        "@unovis/ts",
        "reka-ui",
        "class-variance-authority",
        "xior",
        "clsx",
        "tailwind-merge",
      ],
    },
  },

  modules: ["shadcn-nuxt", '@vee-validate/nuxt', "nuxt-codemirror"],
  veeValidate: {
    autoImports: true,
    componentNames: {
      Form: 'VeeForm',
      Field: 'VeeField',
      FieldArray: 'VeeFieldArray',
      ErrorMessage: 'VeeErrorMessage',
    }
  },
  shadcn: {
    prefix: "",
    componentDir: "@/components/ui",
  },
});
