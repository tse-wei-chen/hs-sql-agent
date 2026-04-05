<script setup lang="ts">
import SignUp from "@/components/SignUp.vue"

definePageMeta({
  layout: "auth",
})
</script>
<template>
  <div class="flex min-h-svh w-full items-center justify-center p-6 md:p-10">
    <div class="w-full max-w-sm">
      <SignUp />
    </div>
  </div>
</template>