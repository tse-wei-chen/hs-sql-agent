<script setup lang="ts">
import { ref } from "vue";
import { toast } from "vue-sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { forgotPassword } from "@/api/auth";
definePageMeta({ layout: "auth" });
const email = ref("");
const sent = ref(false);
const submit = async () => { await forgotPassword(email.value); sent.value = true; toast.success("If the account exists, reset instructions were sent."); };
</script>
<template><div class="flex min-h-svh items-center justify-center p-4"><Card class="w-full max-w-md"><CardHeader><CardTitle>Forgot password</CardTitle><CardDescription>Enter your email. The response is the same whether an account exists or not.</CardDescription></CardHeader><CardContent class="space-y-4"><template v-if="!sent"><div class="space-y-2"><Label for="email">Email</Label><Input id="email" v-model="email" type="email" /></div><Button class="w-full" @click="submit">Send reset instructions</Button></template><p v-else class="text-sm text-muted-foreground">Check your email if the account exists.</p><NuxtLink to="/login" class="block text-center text-sm text-primary hover:underline">Back to login</NuxtLink></CardContent></Card></div></template>
