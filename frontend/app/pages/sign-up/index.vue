<script setup lang="ts">
import SignUp from "@/components/SignUp.vue";

definePageMeta({
  layout: "auth",
});
</script>
<template>
  <div class="relative grid min-h-svh lg:grid-cols-2">
    <div class="absolute inset-0 z-0">
      <div
        class="h-full w-full bg-[url('/hs-background.webp')] bg-cover bg-center dark:brightness-[0.2] dark:grayscale"
        aria-hidden="true"
      ></div>
    </div>

    <div class="relative z-10 flex flex-col gap-4 p-6 md:p-10">
      <div class="flex justify-center gap-2 md:justify-start">
        <a href="#" class="flex items-center gap-2 font-medium">
          <div
            class="bg-primary text-primary-foreground flex size-6 items-center justify-center rounded-md"
          >
            <GalleryVerticalEnd class="size-4" />
          </div>
          hs-sql-agent.
        </a>
      </div>
      <div class="flex flex-1 items-center justify-center">
        <div class="w-full max-w-xs">
          <SignUp />
        </div>
      </div>
    </div>

    <div class="hidden lg:block"></div>
  </div>
</template>
