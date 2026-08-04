<script setup lang="ts">
import { onMounted, ref } from "vue";
import { Monitor, RefreshCw, ShieldX } from "@lucide/vue";
import { toast } from "vue-sonner";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  listSessions,
  revokeOtherSessions,
  revokeSession,
  type AuthSession,
} from "@/api/auth";
import { clearAuthSession } from "@/lib/auth-session";

definePageMeta({ layout: "default" });

const sessions = ref<AuthSession[]>([]);
const loading = ref(false);

const formatDate = (value: string) => new Date(value).toLocaleString();

const load = async () => {
  loading.value = true;
  try {
    sessions.value = await listSessions();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to load sessions.");
  } finally {
    loading.value = false;
  }
};

const revoke = async (session: AuthSession) => {
  if (!confirm(session.isCurrent ? "Sign out this device now?" : "Sign out this session?")) return;
  await revokeSession(session.id);
  if (session.isCurrent) {
    clearAuthSession();
    await navigateTo("/login");
    return;
  }
  toast.success("Session revoked.");
  await load();
};

const revokeOthers = async () => {
  if (!confirm("Sign out every other device?")) return;
  await revokeOtherSessions();
  toast.success("Other sessions revoked.");
  await load();
};

onMounted(load);
</script>

<template>
  <div class="space-y-4">
    <Card>
      <CardHeader class="border-b">
        <CardTitle>Account sessions</CardTitle>
        <CardDescription>Review devices signed in to your account and revoke access immediately.</CardDescription>
      </CardHeader>
      <CardContent class="space-y-4 pt-6">
        <div class="flex justify-end gap-2">
          <Button variant="outline" :disabled="loading" @click="load">
            <RefreshCw class="size-4" /> Refresh
          </Button>
          <Button variant="destructive" :disabled="loading || sessions.length <= 1" @click="revokeOthers">
            <ShieldX class="size-4" /> Sign out other devices
          </Button>
        </div>

        <div v-if="loading" class="py-8 text-center text-sm text-muted-foreground">Loading sessions...</div>
        <div v-else-if="sessions.length === 0" class="py-8 text-center text-sm text-muted-foreground">No active sessions.</div>
        <div v-else class="space-y-3">
          <div v-for="session in sessions" :key="session.id" class="flex flex-col gap-3 rounded-lg border p-4 md:flex-row md:items-center">
            <Monitor class="size-5 shrink-0 text-muted-foreground" />
            <div class="min-w-0 flex-1 text-sm">
              <div class="font-medium">
                {{ session.userAgent || "Unknown device" }}
                <span v-if="session.isCurrent" class="ml-2 rounded-full bg-emerald-100 px-2 py-0.5 text-xs text-emerald-700">Current</span>
              </div>
              <div class="text-muted-foreground">IP: {{ session.ipAddress || "Unknown" }}</div>
              <div class="text-xs text-muted-foreground">Last used {{ formatDate(session.lastUsedAt) }} · Expires {{ formatDate(session.expiresAt) }}</div>
            </div>
            <Button variant="outline" @click="revoke(session)">
              {{ session.isCurrent ? "Sign out" : "Revoke" }}
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
