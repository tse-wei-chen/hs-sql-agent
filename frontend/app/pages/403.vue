<script setup lang="ts">
import { Button } from "@/components/ui/button";
import { ShieldX } from "@lucide/vue";
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-background">
    <div class="flex flex-col items-center gap-4 text-center">
      <ShieldX class="size-16 text-muted-foreground" />
      <h1 class="text-4xl font-bold">403</h1>
      <p class="text-muted-foreground">You don't have permission to access this page.</p>
      <Button as-child>
        <NuxtLink to="/login">Back to Login</NuxtLink>
      </Button>
    </div>
  </div>
</template>
