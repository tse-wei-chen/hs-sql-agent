import tailwindcss from '@tailwindcss/vite'

// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  ssr: false,

  compatibilityDate: '2025-07-15',
  devtools: { enabled: true },
  srcDir: 'app/',

  css: ['~/assets/css/tailwind.css'],

  routeRules: {
    '/api/**': {
      proxy: 'http://localhost:8080/api/**'
    }
  },

  vite: {
    plugins: [
      tailwindcss(),
    ],
  },

  modules: ['shadcn-nuxt'],
  shadcn: {
    prefix: '',
    componentDir: '@/components/ui'
  }
})