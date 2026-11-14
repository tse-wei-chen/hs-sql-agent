<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useAuthStore } from "@/stores/auth";
definePageMeta({ layout: "auth" });
const error = ref("");
const auth = useAuthStore();
onMounted(async () => {
  try {
    const code = String(useRoute().query.code || "");
    if (!code) throw new Error("Missing login code.");
    const outcome = await auth.completeSso(code);
    if (outcome === "mfa") { await navigateTo("/mfa"); return; }
    if (outcome !== "authenticated") throw new Error("SSO sign-in returned no credentials.");
    await navigateTo("/home");
  } catch (reason: any) { error.value = reason?.response?.data?.message || reason?.message || "SSO sign-in failed."; }
});
</script>
<template><div class="flex min-h-svh items-center justify-center p-4"><div class="text-center"><p v-if="!error">Completing SSO sign-in...</p><template v-else><p class="text-destructive">{{ error }}</p><NuxtLink to="/login" class="text-primary hover:underline">Back to login</NuxtLink></template></div></div></template>
