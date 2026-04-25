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
import { checkFirstRun, signUp } from "~/api/admin";

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
  } catch (error) {
    console.error("Failed to check first run status:", error);
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
      return navigateTo("/home");
    }
    alert("Sign up failed. Please try again.");
  } catch (error: any) {
    alert(error?.response?.data || "Sign up failed. Please try again.");
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
    <VeeForm v-slot="{ meta, errors, submitCount }" @submit="submit">
      <FieldGroup>
        <VeeField name="email" rules="required|email" v-slot="{ field, errorMessage, meta: fieldMeta }">
          <UIField class="relative">
            <FieldLabel for="email"> Email </FieldLabel>
            <Input
              v-bind="field"
              id="email"
              type="email"
              placeholder="Enter your email"
            />
            <div class="relative">
              <FieldError v-if="errorMessage && (fieldMeta.touched || submitCount > 0)" class="text-destructive absolute">
                {{ errorMessage }}
              </FieldError>
            </div>
          </UIField>
        </VeeField>

        <VeeField name="password" rules="required|min:8" v-slot="{ field, errorMessage, meta: fieldMeta }">
          <UIField class="relative">
            <FieldLabel for="password"> Password </FieldLabel>
            <PasswordInput
              v-bind="field"
              id="password"
              placeholder="Min 8 characters"
            />
            <div class="relative">
              <FieldError v-if="errorMessage && (fieldMeta.touched || submitCount > 0)" class="text-destructive absolute">
                {{ errorMessage }}
              </FieldError>
            </div>
          </UIField>
        </VeeField>

        <VeeField name="confirmPassword" rules="required|confirmed:@password" v-slot="{ field, errorMessage, meta: fieldMeta }">
          <UIField class="relative">
            <FieldLabel for="confirmPassword"> Confirm Password </FieldLabel>
            <PasswordInput
              v-bind="field"
              id="confirmPassword"
              placeholder="Repeat your password"
            />
            <div class="relative">
              <FieldError v-if="errorMessage && (fieldMeta.touched || submitCount > 0)" class="text-destructive absolute">
                {{ errorMessage }}
              </FieldError>
            </div>
          </UIField>
        </VeeField>

        <UIField>
          <Button type="submit" :disabled="!meta.valid || submitting || checkingFirstRun">
            {{ submitting ? 'Signing up...' : 'Sign Up' }}
          </Button>
        </UIField>
      </FieldGroup>
    </VeeForm>
  </div>
</template>
