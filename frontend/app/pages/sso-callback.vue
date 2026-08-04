<script setup lang="ts">
import { onMounted, ref } from "vue";
import { exchangeOidcCode } from "@/api/auth";
import { persistAuthSession } from "@/lib/auth-session";
definePageMeta({ layout: "auth" });
const error = ref("");
onMounted(async () => {
  try {
    const code = String(useRoute().query.code || "");
    if (!code) throw new Error("Missing login code.");
    const result = await exchangeOidcCode(code);
    if (result.requiresMfa && result.mfaToken) { sessionStorage.setItem("mfaToken", result.mfaToken); await navigateTo("/mfa"); return; }
    persistAuthSession(result);
    await navigateTo("/home");
  } catch (reason: any) { error.value = reason?.response?.data?.message || reason?.message || "SSO sign-in failed."; }
});
</script>
<template><div class="flex min-h-svh items-center justify-center p-4"><div class="text-center"><p v-if="!error">Completing SSO sign-in...</p><template v-else><p class="text-destructive">{{ error }}</p><NuxtLink to="/login" class="text-primary hover:underline">Back to login</NuxtLink></template></div></div></template>
