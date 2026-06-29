<script setup lang="ts">
import type { HTMLAttributes } from "vue";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { FieldGroup, Field as UIField } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import PasswordInput from "@/components/PasswordInput.vue";
import FormField from "@/components/FormField.vue";
import { toast } from "vue-sonner"
import { checkFirstRun, signUp } from "~/api/auth";

const props = defineProps<{
  class?: HTMLAttributes["class"];
}>();

const checkingFirstRun = ref(true);
const submitting = ref(false);

onMounted(async () => {
  try {
    const response = await checkFirstRun();
    if (!response) {
      await navigateTo("/login");
      return;
    }
  } catch {
    return;
  } finally {
    checkingFirstRun.value = false;
  }
});

const submit = async (values: any) => {
  submitting.value = true;
  try {
    const response = await signUp(
      values.email.trim(),
      values.password,
    );
    if (response?.accessToken && response?.refreshToken) {
      localStorage.setItem("accessToken", response.accessToken);
      localStorage.setItem("refreshToken", response.refreshToken);
      localStorage.setItem("userEmail", response.email);
      localStorage.setItem("userName", response.userName);
      localStorage.setItem("permissions", JSON.stringify(response.permissions))
      return await navigateTo("/home");
    }
    toast.error("Sign up failed. Please try again.");
  } catch (error: any) {
    toast.error(error?.response?.data || "Sign up failed. Please try again.");
  } finally {
    submitting.value = false;
  }
};
</script>

<template>
  <div :class="cn('flex flex-col gap-6', props.class)">
    <div class="flex flex-col items-center gap-1 text-center">
      <h1 class="text-2xl font-bold">Sign up</h1>
      <p class="text-muted-foreground text-sm text-balance">
        Please Sign up upon your first login.
      </p>
    </div>
    <VeeForm v-slot="{ meta }" :onSubmit="submit">
      <FieldGroup>
        <FormField name="email" rules="required|email" label="Email" class="relative">
          <template #default="{ field }">
            <Input v-bind="field" id="email" type="email" placeholder="Enter your email" />
          </template>
        </FormField>

        <FormField name="password" rules="required|min:8" label="Password" class="relative" rightAddon>
          <template #default="{ field }">
            <PasswordInput v-bind="field" id="password" placeholder="Min 8 characters" />
          </template>
        </FormField>

        <FormField name="confirmPassword" rules="required|confirmed:@password" label="Confirm Password" class="relative" rightAddon>
          <template #default="{ field }">
            <PasswordInput v-bind="field" id="confirmPassword" placeholder="Repeat your password" />
          </template>
        </FormField>

        <UIField>
          <Button type="submit" :disabled="!meta.valid || submitting || checkingFirstRun">
            {{ submitting ? 'Signing up...' : 'Sign Up' }}
          </Button>
        </UIField>
      </FieldGroup>
    </VeeForm>
  </div>
</template>
