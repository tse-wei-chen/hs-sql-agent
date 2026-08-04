<script setup lang="ts">
import { onMounted, ref } from "vue";
import { Monitor, RefreshCw, ShieldX } from "@lucide/vue";
import { toast } from "vue-sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  listSessions,
  getAccount,
  updateAccount,
  changePassword,
  getMfaStatus,
  beginMfaSetup,
  confirmMfaSetup,
  disableMfa,
  revokeOtherSessions,
  revokeSession,
  type AuthSession,
} from "@/api/auth";
import { clearAuthSession } from "@/lib/auth-session";

definePageMeta({ layout: "default" });

const sessions = ref<AuthSession[]>([]);
const loading = ref(false);
const username = ref("");
const email = ref("");
const currentPassword = ref("");
const newPassword = ref("");
const passwordChangeRequired = ref(false);
const mfaEnabled = ref(false);
const mfaEnrollmentRequired = ref(false);
const recoveryCodesRemaining = ref(0);
const mfaSetupSecret = ref("");
const mfaSetupUri = ref("");
const mfaCode = ref("");
const recoveryCodes = ref<string[]>([]);

const tokenRequiresMfaEnrollment = () => {
  try {
    const payload = JSON.parse(atob((localStorage.getItem("accessToken")?.split(".")[1] || "").replace(/-/g, "+").replace(/_/g, "/")));
    return payload?.mfa_enrollment_required === "true";
  } catch { return false; }
};

const formatDate = (value: string) => new Date(value).toLocaleString();

const load = async () => {
  loading.value = true;
  try {
    const profile = await getAccount();
    username.value = profile.username;
    email.value = profile.mail;
    passwordChangeRequired.value = profile.requirePasswordChangeAtNextSignIn;
    const mfa = await getMfaStatus();
    mfaEnabled.value = mfa.enabled;
    recoveryCodesRemaining.value = mfa.recoveryCodesRemaining;
    mfaEnrollmentRequired.value = tokenRequiresMfaEnrollment();
    sessions.value = passwordChangeRequired.value || mfaEnrollmentRequired.value ? [] : await listSessions();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to load sessions.");
  } finally {
    loading.value = false;
  }
};

const startMfaSetup = async () => {
  try {
    const setup = await beginMfaSetup();
    mfaSetupSecret.value = setup.secret;
    mfaSetupUri.value = setup.otpAuthUri;
  } catch (error: any) { toast.error(error?.response?.data || "Failed to start MFA setup."); }
};

const confirmMfa = async () => {
  try {
    const result = await confirmMfaSetup(mfaCode.value);
    recoveryCodes.value = result.recoveryCodes;
    mfaEnabled.value = true;
    toast.success("MFA enabled. Save the recovery codes before signing in again.");
  } catch (error: any) { toast.error(error?.response?.data || "Invalid authenticator code."); }
};

const turnOffMfa = async () => {
  try {
    await disableMfa(mfaCode.value);
    clearAuthSession();
    await navigateTo("/login");
  } catch (error: any) { toast.error(error?.response?.data || "Failed to disable MFA."); }
};

const finishMfaSetup = async () => {
  clearAuthSession();
  await navigateTo("/login");
};

const saveProfile = async () => {
  try {
    await updateAccount(username.value, email.value);
    toast.success("Account updated. Please sign in again.");
    clearAuthSession();
    await navigateTo("/login");
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to update account.");
  }
};

const savePassword = async () => {
  try {
    await changePassword(currentPassword.value, newPassword.value);
    toast.success("Password changed. Please sign in again.");
    clearAuthSession();
    await navigateTo("/login");
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to change password.");
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
        <CardTitle>Multi-factor authentication</CardTitle>
        <CardDescription v-if="mfaEnrollmentRequired" class="text-destructive">Your role requires TOTP MFA before you can continue.</CardDescription>
        <CardDescription v-else>Use an authenticator app and one-time recovery codes.</CardDescription>
      </CardHeader>
      <CardContent class="space-y-4 pt-6">
        <template v-if="recoveryCodes.length">
          <p class="font-medium">Save these recovery codes now. They will not be shown again.</p>
          <pre class="rounded border bg-muted p-4 text-sm">{{ recoveryCodes.join("\n") }}</pre>
          <Button @click="finishMfaSetup">I saved them — sign in again</Button>
        </template>
        <template v-else-if="!mfaEnabled">
          <Button v-if="!mfaSetupSecret" @click="startMfaSetup">Set up authenticator</Button>
          <template v-else>
            <div class="space-y-2"><Label>Setup key</Label><code class="block break-all rounded border p-3">{{ mfaSetupSecret }}</code></div>
            <div class="space-y-2"><Label>Authenticator URI</Label><code class="block break-all rounded border p-3 text-xs">{{ mfaSetupUri }}</code></div>
            <div class="space-y-2"><Label for="mfa-confirm">6-digit code</Label><Input id="mfa-confirm" v-model="mfaCode" autocomplete="one-time-code" /></div>
            <Button @click="confirmMfa">Enable MFA</Button>
          </template>
        </template>
        <template v-else>
          <p class="text-sm text-muted-foreground">MFA is enabled. {{ recoveryCodesRemaining }} recovery codes remain.</p>
          <div class="flex gap-2"><Input v-model="mfaCode" placeholder="Authenticator or recovery code" /><Button variant="destructive" @click="turnOffMfa">Disable MFA</Button></div>
        </template>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Account profile</CardTitle>
        <CardDescription>Email is the unique sign-in identity; usernames are display names and may be shared.</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-4 pt-6 md:grid-cols-2">
        <div class="space-y-2"><Label for="username">Username</Label><Input id="username" v-model="username" maxlength="100" /></div>
        <div class="space-y-2"><Label for="email">Email</Label><Input id="email" v-model="email" type="email" maxlength="320" /></div>
        <div><Button :disabled="passwordChangeRequired" @click="saveProfile">Save profile</Button></div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Change password</CardTitle>
        <CardDescription v-if="passwordChangeRequired" class="text-destructive">An administrator requires you to change your password before continuing.</CardDescription>
        <CardDescription v-else>Changing your password signs out every active device.</CardDescription>
      </CardHeader>
      <CardContent class="grid gap-4 pt-6 md:grid-cols-2">
        <div class="space-y-2"><Label for="current-password">Current password</Label><Input id="current-password" v-model="currentPassword" type="password" /></div>
        <div class="space-y-2"><Label for="new-password">New password</Label><Input id="new-password" v-model="newPassword" type="password" minlength="8" /></div>
        <div><Button @click="savePassword">Change password</Button></div>
      </CardContent>
    </Card>

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
