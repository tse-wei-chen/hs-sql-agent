<script setup lang="ts">
import { ref } from "vue";
import { toast } from "vue-sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { resetPassword } from "@/api/auth";
definePageMeta({ layout: "auth" });
const route = useRoute();
const password = ref("");
const submit = async () => { try { await resetPassword(String(route.query.token || ""), password.value); toast.success("Password reset. You can sign in now."); await navigateTo("/login"); } catch (error: any) { toast.error(error?.response?.data || "Reset link is invalid or expired."); } };
</script>
<template><div class="flex min-h-svh items-center justify-center p-4"><Card class="w-full max-w-md"><CardHeader><CardTitle>Reset password</CardTitle><CardDescription>Reset links expire and can be used only once.</CardDescription></CardHeader><CardContent class="space-y-4"><div class="space-y-2"><Label for="password">New password</Label><Input id="password" v-model="password" type="password" minlength="8" /></div><Button class="w-full" :disabled="password.length < 8 || !route.query.token" @click="submit">Reset password</Button></CardContent></Card></div></template>
