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
import { checkFirstRun, signIn } from "~/api/admin"

const props = defineProps<{
  class?: HTMLAttributes["class"]
}>()
let loginData: Ref<{ email: string; password: string }> = ref({
  email: "",
  password: "",
})

onMounted(async () => {
  try {
    const response = await checkFirstRun()
    if (response) {
      await navigateTo("/sign-up")
    }
  } catch (error) {
    console.error("Failed to check first run status:", error)
  }
})

const submit = async () => {
  try {
    const response = await signIn(loginData.value.email, loginData.value.password)

    if (response?.changePasswordToken) {
      localStorage.setItem("changePasswordToken", response.changePasswordToken)
      return navigateTo("/change-password")
    }

    if (response?.accessToken && response?.refreshToken) {
      localStorage.setItem("accessToken", response.accessToken)
      localStorage.setItem("refreshToken", response.refreshToken)
      window.location.href = "/dashboard"
      return
    }

    alert("Login failed. Please try again.")
  } catch (error: any) {
    alert(error?.response?.data || "Login failed. Please try again.")
  }
}
</script>

<template>
  <div :class="cn('flex flex-col gap-6', props.class)">
    <Card>
      <CardHeader>
        <CardTitle>SQL Agent Tool Admin Panel</CardTitle>
        <CardDescription>
          Enter your email below to login to your account
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form>
          <FieldGroup>
            <Field>
              <FieldLabel for="email">
                Email
              </FieldLabel>
              <Input
                id="email"
                type="email"
                placeholder="m@example.com"
                required
                v-model="loginData.email"
              />
            </Field>
            <Field>
            <FieldLabel for="password">
                Password
            </FieldLabel>
              <Input v-model="loginData.password" id="password" type="password" required />
            </Field>
            <Field>
              <Button type="submit" @click.prevent="submit">
                Login
              </Button>
            </Field>
          </FieldGroup>
        </form>
      </CardContent>
    </Card>
  </div>
</template>
