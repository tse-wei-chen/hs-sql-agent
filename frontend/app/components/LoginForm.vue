<script setup lang="ts">
import type { HTMLAttributes } from "vue";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { FieldGroup, Field as UIField } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import PasswordInput from "@/components/PasswordInput.vue";
import FormField from "@/components/FormField.vue";
import { toast } from "vue-sonner"
import { checkFirstRun, getOidcStatus, signIn } from "~/api/auth";
import { persistAuthSession } from "@/lib/auth-session";

const props = defineProps<{
  class?: HTMLAttributes["class"];
}>();

const submitting = ref(false);
const oidcEnabled = ref(false);
const startSso = () => window.location.assign("/api/auth/oidc/login");

onMounted(async () => {
  try {
    const response = await checkFirstRun();
    oidcEnabled.value = (await getOidcStatus()).enabled;
    if (response) {
      return await navigateTo("/sign-up");
    }
  } catch {
    // not first run
  }
});

const submit = async (values: any) => {
  submitting.value = true;
  try {
    const response = await signIn(
      values.email.trim(),
      values.password,
    );

    if (response?.accessToken && response?.refreshToken) {
      persistAuthSession(response);
      return await navigateTo("/home");
    }
    if (response?.requiresMfa && response?.mfaToken) {
      sessionStorage.setItem("mfaToken", response.mfaToken);
      return await navigateTo("/mfa");
    }

    toast.error("Login failed. Please try again.");
  } catch (error: any) {
    toast.error(error?.response?.data || "Login failed. Please try again.");
  } finally {
    submitting.value = false;
  }
};
</script>

<template>
  <div :class="cn('flex flex-col gap-6', props.class)">
    <VeeForm v-slot="{ meta }" :onSubmit="submit">
      <FieldGroup>
        <div class="flex flex-col items-center gap-1 text-center">
          <h1 class="text-2xl font-bold">HS Admin Panel</h1>
          <p class="text-muted-foreground text-sm text-balance">
            Enter your email below to login to your account
          </p>
        </div>
        
        <FormField name="email" rules="required|email" label="Email" class="relative">
          <template #default="{ field }">
            <Input v-bind="field" id="email" type="email" placeholder="m@example.com" />
          </template>
        </FormField>

        <FormField name="password" rules="required" label="Password" class="relative" rightAddon>
          <template #default="{ field }">
            <PasswordInput v-bind="field" id="password" />
          </template>
        </FormField>
        <NuxtLink to="/forgot-password" class="text-right text-sm text-primary hover:underline">Forgot password?</NuxtLink>

        <UIField>
          <Button type="submit" :disabled="!meta.valid || submitting">
            {{ submitting ? 'Logging in...' : 'Login' }}
          </Button>
        </UIField>
        <template v-if="oidcEnabled">
          <div class="relative text-center text-xs text-muted-foreground before:absolute before:left-0 before:right-0 before:top-1/2 before:border-t"><span class="relative bg-background px-2">or</span></div>
          <Button type="button" variant="outline" @click="startSso">Sign in with SSO</Button>
        </template>
      </FieldGroup>
    </VeeForm>
  </div>
</template>
