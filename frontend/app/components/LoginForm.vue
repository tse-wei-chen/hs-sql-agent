<script setup lang="ts">
import type { HTMLAttributes } from "vue";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  Field as UIField,
  FieldGroup,
  FieldLabel,
  FieldError,
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import PasswordInput from "@/components/PasswordInput.vue";
import { checkFirstRun, signIn } from "~/api/admin";

const props = defineProps<{
  class?: HTMLAttributes["class"];
}>();

const submitting = ref(false);

onMounted(async () => {
  try {
    const response = await checkFirstRun();
    if (response) {
      return await navigateTo("/sign-up");
    }
  } catch (error) {
    console.error("Failed to check first run status:", error);
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
      localStorage.setItem("accessToken", response.accessToken);
      localStorage.setItem("refreshToken", response.refreshToken);
      localStorage.setItem("userEmail", response.email);
      localStorage.setItem("userName", response.userName);
      return await navigateTo("/home");
    }

    alert("Login failed. Please try again.");
  } catch (error: any) {
    alert(error?.response?.data || "Login failed. Please try again.");
  } finally {
    submitting.value = false;
  }
};
</script>

<template>
  <div :class="cn('flex flex-col gap-6', props.class)">
    <VeeForm v-slot="{ meta, errors, submitCount }" @submit="submit">
      <FieldGroup>
        <div class="flex flex-col items-center gap-1 text-center">
          <h1 class="text-2xl font-bold">HS Admin Panel</h1>
          <p class="text-muted-foreground text-sm text-balance">
            Enter your email below to login to your account
          </p>
        </div>
        
        <VeeField name="email" rules="required|email" v-slot="{ field, errorMessage, meta: fieldMeta }">
          <UIField class="relative">
            <FieldLabel for="email"> Email </FieldLabel>
            <Input
              v-bind="field"
              id="email"
              type="email"
              placeholder="m@example.com"
            />
            <div class="relative">
              <FieldError v-if="errorMessage && (fieldMeta.touched || submitCount > 0)" class="text-destructive absolute">
                {{ errorMessage }}
              </FieldError>
            </div>
          </UIField>
        </VeeField>

        <VeeField name="password" rules="required" v-slot="{ field, errorMessage, meta: fieldMeta }">
          <UIField class="relative">
            <FieldLabel for="password"> Password </FieldLabel>
            <PasswordInput
              v-bind="field"
              id="password"
            />
            <div class="relative">
              <FieldError v-if="errorMessage && (fieldMeta.touched || submitCount > 0)" class="text-destructive absolute">
                {{ errorMessage }}
              </FieldError>
            </div>
          </UIField>
        </VeeField>

        <UIField>
          <Button type="submit" :disabled="!meta.valid || submitting">
            {{ submitting ? 'Logging in...' : 'Login' }}
          </Button>
        </UIField>
      </FieldGroup>
    </VeeForm>
  </div>
</template>
