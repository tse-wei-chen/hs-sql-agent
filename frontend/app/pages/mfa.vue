<script setup lang="ts">
import { ref } from "vue";
import { toast } from "vue-sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { verifyMfa } from "@/api/auth";
import { persistAuthSession } from "@/lib/auth-session";
definePageMeta({ layout: "auth" });
const code = ref(""); const submitting = ref(false);
const submit = async () => { submitting.value = true; try { const token = sessionStorage.getItem("mfaToken"); if (!token) throw new Error("MFA challenge expired."); const result = await verifyMfa(token, code.value); sessionStorage.removeItem("mfaToken"); persistAuthSession(result); await navigateTo("/home"); } catch (error: any) { toast.error(error?.response?.data?.message || error?.message || "Verification failed."); } finally { submitting.value = false; } };
</script>
<template><div class="flex min-h-svh items-center justify-center p-4"><Card class="w-full max-w-md"><CardHeader><CardTitle>Multi-factor authentication</CardTitle><CardDescription>Enter a 6-digit authenticator code or one recovery code.</CardDescription></CardHeader><CardContent class="space-y-4"><div class="space-y-2"><Label for="mfa-code">Verification code</Label><Input id="mfa-code" v-model="code" autocomplete="one-time-code" /></div><Button class="w-full" :disabled="!code || submitting" @click="submit">Verify</Button></CardContent></Card></div></template>
