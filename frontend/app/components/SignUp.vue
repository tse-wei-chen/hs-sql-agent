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
import { signUp } from "~/api/admin"

const props = defineProps<{
  class?: HTMLAttributes["class"]
}>()
let loginData: Ref<{ email: string; password: string }> = ref({
  email: "",
  password: "",
})


const submit = async () => {
  try {
    const response = await signUp(loginData.value.email, loginData.value.password)
    if (response?.accessToken && response?.refreshToken) {
      localStorage.setItem("accessToken", response.accessToken)
      localStorage.setItem("refreshToken", response.refreshToken)
      return navigateTo("/home")
    }
    alert("Sign up failed. Please try again.")
  } catch (error: any) {
    alert(error?.response?.data || "Sign up failed. Please try again.")
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
        <form>
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
                Sign Up
              </Button>
            </Field>
          </FieldGroup>
        </form>
      </CardContent>
    </Card>
  </div>
</template>
