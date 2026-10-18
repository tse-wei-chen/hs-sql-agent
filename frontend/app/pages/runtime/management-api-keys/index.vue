<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref } from "vue";
import { toast } from "vue-sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from "@/components/ui/card";
import {
  issueManagementApiKey, listManagementApiKeys, listManagementKeyPermissions,
  revokeManagementApiKey, rotateManagementApiKey,
  type ManagementApiKey, type IssuedManagementApiKey
} from "@/api/management-api-keys";

definePageMeta({ layout: "default", permission: "/runtime/security.edit" });

const keys = ref<ManagementApiKey[]>([]);
const catalog = ref<string[]>([]);
const selected = ref<string[]>([]);
const name = ref("");
const expiry = ref(new Date(Date.now() + 90 * 24 * 60 * 60 * 1000).toISOString().slice(0, 16));
const plaintext = ref("");
const busy = ref(false);
const error = ref("");

const load = async () => {
  error.value = "";
  try {
    const [items, permissions] = await Promise.all([
      listManagementApiKeys(), listManagementKeyPermissions()
    ]);
    keys.value = items;
    catalog.value = permissions;
  } catch {
    error.value = "Unable to load Management API Keys. Check your security edit permission.";
  }
};

const reveal = (result: IssuedManagementApiKey) => {
  plaintext.value = result.plaintextKey;
  toast.success("Copy the secret now. It cannot be retrieved later.");
};

const issue = async () => {
  if (!name.value.trim() || !selected.value.length) return;
  busy.value = true;
  plaintext.value = "";
  try {
    const result = await issueManagementApiKey({
      name: name.value.trim(),
      expiresAt: new Date(expiry.value + "Z").toISOString(),
      permissions: selected.value
    });
    reveal(result);
    name.value = "";
    await load();
  } catch (e: any) {
    toast.error(e?.response?.data?.error || "Unable to issue key.");
  } finally { busy.value = false; }
};

const rotate = async (item: ManagementApiKey) => {
  if (!confirm(`Rotate "${item.name}"? The existing key will be revoked immediately.`)) return;
  busy.value = true;
  plaintext.value = "";
  try {
    reveal(await rotateManagementApiKey(item.id));
    await load();
  } catch { toast.error("Unable to rotate key."); }
  finally { busy.value = false; }
};

const revoke = async (item: ManagementApiKey) => {
  if (!confirm(`Revoke "${item.name}"? This cannot be undone.`)) return;
  busy.value = true;
  try {
    await revokeManagementApiKey(item.id);
    await load();
    toast.success("Management API Key revoked.");
  } catch { toast.error("Unable to revoke key."); }
  finally { busy.value = false; }
};

const copy = async () => {
  try { await navigator.clipboard.writeText(plaintext.value); toast.success("Secret copied."); }
  catch { toast.error("Copy failed. Select and copy the secret manually."); }
};

const active = (key: ManagementApiKey) =>
  !key.revokedAt && new Date(key.expiresAt).getTime() > Date.now();
const formatDate = (s: string | null) => s ? new Date(s).toLocaleString() : "Never";

onMounted(load);
onBeforeUnmount(() => { plaintext.value = ""; });
</script>

<template>
  <div class="space-y-5">
    <div>
      <h1 class="text-2xl font-semibold">Management API Keys</h1>
      <p class="text-sm text-muted-foreground">
        Authorize backend services to call selected /api administration endpoints.
        These keys are never valid for /mcp and cannot manage user accounts or other Management API Keys.
      </p>
    </div>

    <Card v-if="plaintext">
      <CardHeader>
        <CardTitle>New secret — shown only once</CardTitle>
        <CardDescription>Store this key in your backend secret manager, not a browser or repository.</CardDescription>
      </CardHeader>
      <CardContent class="space-y-3">
        <div class="break-all rounded-md border p-3 font-mono text-xs select-all">{{ plaintext }}</div>
        <div class="flex gap-2">
          <Button @click="copy">Copy secret</Button>
          <Button variant="outline" @click="plaintext = ''">Hide secret</Button>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>Issue Management API Key</CardTitle>
        <CardDescription>Permissions must be explicitly selected; no implicit administrator access.</CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-3 sm:grid-cols-2">
          <label class="space-y-1 text-sm">Integration name
            <Input v-model="name" maxlength="100" placeholder="SaaS provisioning backend" />
          </label>
          <label class="space-y-1 text-sm">Expires (UTC)
            <Input v-model="expiry" type="datetime-local" />
          </label>
        </div>
        <fieldset class="space-y-2">
          <legend class="text-sm font-medium">Allowed API permissions ({{ selected.length }} selected)</legend>
          <div class="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
            <label v-for="grant in catalog" :key="grant" class="flex items-center gap-2 rounded-md border p-2 text-xs">
              <input v-model="selected" type="checkbox" :value="grant" />
              <span class="break-all">{{ grant }}</span>
            </label>
          </div>
        </fieldset>
        <Button :disabled="busy || !name.trim() || !selected.length || !expiry" @click="issue">
          Issue key
        </Button>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <CardTitle>Issued keys</CardTitle>
        <CardDescription>Revocation takes effect on the next authenticated request. Rotation revokes the old key immediately.</CardDescription>
      </CardHeader>
      <CardContent class="space-y-2">
        <p v-if="error" role="alert" class="text-sm text-destructive">{{ error }}</p>
        <p v-if="!keys.length" class="text-sm text-muted-foreground">No keys issued yet.</p>
        <div v-for="item in keys" :key="item.id" class="flex flex-wrap items-center justify-between gap-3 rounded-md border p-3">
          <div class="space-y-1">
            <div class="font-medium">{{ item.name }}
              <span class="ml-2 text-xs text-muted-foreground">{{ active(item) ? "Active" : "Inactive" }}</span>
            </div>
            <div class="text-xs text-muted-foreground font-mono">{{ item.keyPrefix }}…</div>
            <p class="text-xs text-muted-foreground">
              Expires {{ formatDate(item.expiresAt) }} · Last used {{ formatDate(item.lastUsedAt) }}
            </p>
            <div class="text-xs text-muted-foreground">{{ item.permissions.join(", ") }}</div>
          </div>
          <div class="flex gap-2">
            <Button variant="outline" size="sm" :disabled="busy || !active(item)" @click="rotate(item)">Rotate</Button>
            <Button variant="destructive" size="sm" :disabled="busy || !active(item)" @click="revoke(item)">Revoke</Button>
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
