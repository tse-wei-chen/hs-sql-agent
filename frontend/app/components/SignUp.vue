<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import {
	Card,
	CardContent,
	CardDescription,
	CardHeader,
	CardTitle,
} from "@/components/ui/card"
import {
	Field,
	FieldDescription,
	FieldGroup,
	FieldLabel,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { checkFirstRun, signUp } from "~/api/admin"

const props = defineProps<{
  class?: HTMLAttributes["class"]
}>()
let loginData: Ref<{ email: string; password: string }> = ref({
  email: "",
  password: "",
})
const emailTouched = ref(false)
const passwordTouched = ref(false)
const hasSubmitted = ref(false)
const checkingFirstRun = ref(true)
const submitting = ref(false)

const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

const emailError = computed(() => {
  const email = loginData.value.email.trim()
  if (!email) {
    return "Email is required."
  }
  if (!emailPattern.test(email)) {
    return "Please enter a valid email address."
  }
  return ""
})

const passwordError = computed(() => {
  if (!loginData.value.password.trim()) {
    return "Password is required."
  }
  return ""
})

const canSubmit = computed(() => {
  return (
    !checkingFirstRun.value
    && !submitting.value
    && !emailError.value
    && !passwordError.value
  )
})

onMounted(async () => {
  try {
    const response = await checkFirstRun()
    if (!response) {
      await navigateTo("/login")
      return
    }
  } catch (error) {
    console.error("Failed to check first run status:", error)
    return
  } finally {
    checkingFirstRun.value = false
  }
})


const submit = async () => {
  hasSubmitted.value = true
  if (!canSubmit.value) {
    return
  }

  submitting.value = true
  try {
    const response = await signUp(
      loginData.value.email.trim(),
      loginData.value.password,
    )
    if (response?.accessToken && response?.refreshToken) {
      localStorage.setItem("accessToken", response.accessToken)
      localStorage.setItem("refreshToken", response.refreshToken)
      localStorage.setItem("userEmail", response.email)
      localStorage.setItem("userName", response.userName)
      return navigateTo("/home")
    }
    alert("Sign up failed. Please try again.")
  } catch (error: any) {
    alert(error?.response?.data || "Sign up failed. Please try again.")
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div :class="cn('flex flex-col gap-6', props.class)">
    <Card>
      <CardHeader>
        <CardTitle>Sign up</CardTitle>
        <CardDescription>
          Please Sign up upon your first login.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form @submit.prevent="submit">
          <FieldGroup>
            <Field>
              <FieldLabel for="email">
                Email
              </FieldLabel>
              <Input
                id="email"
                type="email"
                placeholder="Enter your email"
                required
                v-model="loginData.email"
                @blur="emailTouched = true"
              />
              <FieldDescription
                v-if="emailError && (emailTouched || hasSubmitted)"
                class="text-destructive"
              >
                {{ emailError }}
              </FieldDescription>
            </Field>
            <Field>
            <FieldLabel for="password">
                Password
            </FieldLabel>
              <Input
                v-model="loginData.password"
                id="password"
                type="password"
                required
                @blur="passwordTouched = true"
              />
              <FieldDescription
                v-if="passwordError && (passwordTouched || hasSubmitted)"
                class="text-destructive"
              >
                {{ passwordError }}
              </FieldDescription>
            </Field>
            <Field>
              <Button type="submit" :disabled="!canSubmit">
                Sign Up
              </Button>
            </Field>
          </FieldGroup>
        </form>
      </CardContent>
    </Card>
  </div>
</template>
