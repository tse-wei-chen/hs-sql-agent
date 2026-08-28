import tailwindcss from "@tailwindcss/vite";

// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  ssr: false,

  compatibilityDate: "2025-07-15",
  devtools: { enabled: true },
  srcDir: "app/",

  colorMode: {
    preference: 'system',
    fallback: 'dark',
    classSuffix: ''
  },

  css: ["~/assets/css/tailwind.css", "vue-sonner/style.css"],

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
    "/mcp": {
      proxy: "http://localhost:8080/mcp",
    },
    "/mcp/**": {
      proxy: "http://localhost:8080/mcp/**",
    },
  },

  $production: {
    routeRules: {}
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
        "@codemirror/lang-json",
        "@codemirror/state",
        "@codemirror/view",
        "@codemirror/commands",
        "@codemirror/theme-one-dark",
        "codemirror",
        "@lucide/vue",
        "vue-sonner"
      ],
    },
  },

  modules: [
    "shadcn-nuxt",
    '@vee-validate/nuxt',
    "@nuxtjs/color-mode",
    "@nuxt/eslint",
  ],
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
