<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Checkbox } from "@/components/ui/checkbox";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Edit2, LogOut, Power, PowerOff, Save, Trash2, UserPlus, X } from "@lucide/vue";
import FormField from "@/components/FormField.vue";
import PasswordInput from "@/components/PasswordInput.vue";
import {
  createMember,
  deleteMember,
  listMembers,
  updateMemberRoles,
  updateMemberStatus,
  revokeMemberSessions,
  type Member,
} from "~/api/member";
import {
  listRoles,
  type Role,
} from "~/api/role";
import { toast } from "vue-sonner"
import { useForm } from "vee-validate";

const currentUserId = (() => {
  try {
    const token = localStorage.getItem("accessToken");
    if (!token) return null;
    const payloadBase64 = token.split(".")[1];
    if (!payloadBase64) return null;
    const payload = JSON.parse(atob(payloadBase64));
    return payload.sub ? Number(payload.sub) : null;
  } catch {
    return null;
  }
})();

definePageMeta({
  layout: "default",
  permission: "/auth/user.view",
});

interface UserFormValues {
  email: string;
  username: string;
  password: string;
}

const { meta, values, setValues, resetForm: resetVeeForm, handleSubmit } = useForm<UserFormValues>({
  initialValues: {
    email: "",
    username: "",
    password: "",
  },
});

const members = ref<Member[]>([]);
const roles = ref<Role[]>([]);
const selectedRoleIds = ref<number[]>([]);
const assignAllRoles = ref(false);
const loading = ref(false);
const saving = ref(false);
const editingId = ref<number | null>(null);
const loadError = ref("");

const roleMap = computed(() => new Map(roles.value.map((role) => [role.id, role])));

const selectedRoleNames = computed(() => {
  return selectedRoleIds.value
    .map((id) => roleMap.value.get(id)?.name)
    .filter(Boolean) as string[];
});

const toggleRole = (roleId: number, checked: boolean | "indeterminate") => {
  if (checked === true) {
    if (!selectedRoleIds.value.includes(roleId)) selectedRoleIds.value.push(roleId);
    return;
  }

  selectedRoleIds.value = selectedRoleIds.value.filter((id) => id !== roleId);
};

const memberRoleNames = (member: Member) => {
  if (member.roles?.length) return member.roles;
  return (member.roleIds ?? [])
    .map((id) => roleMap.value.get(id)?.name)
    .filter(Boolean) as string[];
};

const load = async () => {
  loading.value = true;
  loadError.value = "";
  try {
    const [memberData, roleData] = await Promise.all([listMembers(), listRoles()]);
    members.value = memberData;
    roles.value = roleData;
  } catch (error: any) {
    loadError.value = error?.response?.data || "Failed to load users.";
  } finally {
    loading.value = false;
  }
};

const resetForm = () => {
  resetVeeForm();
  selectedRoleIds.value = [];
  assignAllRoles.value = false;
  editingId.value = null;
};

const startEdit = (member: Member) => {
  editingId.value = member.id;
  setValues({
    email: member.mail,
    username: member.username,
    password: "",
  });
  selectedRoleIds.value = [...(member.roleIds ?? [])];
  assignAllRoles.value = false;
  window.scrollTo({ top: 0, behavior: "smooth" });
};

const save = async () => {
  saving.value = true;
  try {
    if (editingId.value) {
      await updateMemberRoles(editingId.value, assignAllRoles.value ? roles.value.map((role) => role.id) : selectedRoleIds.value);
    } else {
      await createMember({
        email: values.email.trim(),
        username: values.username?.trim() || undefined,
        password: values.password,
        assignAllRoles: assignAllRoles.value,
        roleIds: selectedRoleIds.value,
      });
    }

    resetForm();
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to save user.");
  } finally {
    saving.value = false;
  }
};

const onSave = handleSubmit(save);

const remove = async (member: Member) => {
  if (!confirm(`Delete user "${member.mail}"?`)) return;

  try {
    await deleteMember(member.id);
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to delete user.");
  }
};

const toggleStatus = async (member: Member) => {
  const action = member.isActive ? "disable" : "enable";
  if (!confirm(`${action[0].toUpperCase()}${action.slice(1)} user "${member.mail}"?`)) return;

  try {
    await updateMemberStatus(member.id, !member.isActive);
    await load();
    toast.success(`User ${action}d.`);
  } catch (error: any) {
    toast.error(error?.response?.data || `Failed to ${action} user.`);
  }
};

const revokeSessions = async (member: Member) => {
  if (!confirm(`Sign out all sessions for "${member.mail}"?`)) return;
  try {
    await revokeMemberSessions(member.id);
    toast.success("All user sessions were revoked.");
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to revoke user sessions.");
  }
};

onMounted(load);
</script>

<template>
  <div class="space-y-6">
    <Card>
      <CardHeader class="border-b">
        <CardTitle>{{ editingId ? "Edit User Roles" : "Create User" }}</CardTitle>
        <CardDescription>
          Create a member account and assign one or more roles.
        </CardDescription>
      </CardHeader>
      <CardContent class="pt-6">
        <form class="space-y-6" @submit.prevent="onSave">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <FormField name="email" rules="required|email" label="Email">
              <template #default="{ field }">
                <Input v-bind="field" id="email" type="email" placeholder="operator@example.com" :disabled="!!editingId" />
              </template>
            </FormField>

            <FormField name="username" label="Username">
              <template #default="{ field }">
                <Input v-bind="field" id="username" placeholder="operator" :disabled="!!editingId" />
              </template>
            </FormField>

            <FormField v-if="!editingId" name="password" rules="required|min:8" label="Password" class="md:col-span-2" rightAddon>
              <template #default="{ field }">
                <PasswordInput v-bind="field" id="password" placeholder="At least 8 characters" />
              </template>
            </FormField>
          </FieldGroup>

          <Field>
            <div class="mb-3 flex items-center justify-between">
              <FieldLabel>Roles</FieldLabel>
              <span class="text-xs text-muted-foreground">
                {{ assignAllRoles ? "All roles" : `${selectedRoleIds.length} selected` }}
              </span>
            </div>

            <div class="mb-3 flex items-center gap-2 rounded-md border bg-muted/20 p-3">
              <Checkbox
                id="assignAllRoles"
                v-model="assignAllRoles"
              />
              <Label for="assignAllRoles" class="cursor-pointer text-sm">
                Assign all roles
              </Label>
            </div>

            <div v-if="loading" class="rounded-md border p-4 text-sm text-muted-foreground">
              Loading roles...
            </div>
            <div v-else-if="loadError" class="rounded-md border border-destructive/30 bg-destructive/5 p-4 text-sm text-destructive">
              {{ loadError }}
            </div>
            <div v-else-if="roles.length === 0" class="rounded-md border p-4 text-sm text-muted-foreground">
              No roles defined yet.
            </div>
            <div v-else class="grid gap-3 md:grid-cols-2">
              <label
                v-for="role in roles"
                :key="role.id"
                class="flex cursor-pointer items-start gap-3 rounded-md border p-3 transition-colors hover:border-primary/50"
                :class="assignAllRoles || selectedRoleIds.includes(role.id) ? 'bg-primary/5 border-primary/40' : 'bg-card'"
              >
                <Checkbox
                  :disabled="assignAllRoles"
                  v-model="computed({
                    get: () => assignAllRoles || selectedRoleIds.includes(role.id),
                    set: (val) => toggleRole(role.id, val)
                  }).value"
                />
                <span>
                  <span class="block text-sm font-medium">{{ role.name }}</span>
                  <span class="block text-xs text-muted-foreground">{{ role.description || "No description" }}</span>
                </span>
              </label>
            </div>
          </Field>

          <div v-if="selectedRoleNames.length" class="flex flex-wrap gap-1.5">
            <Badge v-for="name in selectedRoleNames" :key="name" variant="outline">
              {{ name }}
            </Badge>
          </div>

          <div class="flex justify-end gap-2 pt-2">
            <Button v-if="editingId" type="button" variant="ghost" @click="resetForm">
              <X class="mr-2 size-4" /> Cancel
            </Button>
            <Button type="submit" :disabled="(!editingId && !meta.valid) || saving" v-permission="[editingId ? 'edit' : 'create']">
              <Save v-if="!saving" class="mr-2 size-4" />
              {{ saving ? "Saving..." : editingId ? "Update Roles" : "Create User" }}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Registered Users</CardTitle>
      </CardHeader>
      <CardContent class="pt-6">
        <div v-if="loading" class="py-8 text-center text-sm text-muted-foreground">
          Loading users...
        </div>
        <div v-else-if="members.length === 0" class="py-8 text-center text-sm text-muted-foreground">
          No users defined yet.
        </div>
        <div v-else class="grid gap-4 lg:grid-cols-2">
          <div
            v-for="member in members"
            :key="member.id"
            class="group flex flex-col rounded-lg border bg-card p-4 shadow-sm transition-colors hover:border-primary/50"
          >
            <div class="mb-3 flex items-start justify-between gap-3">
              <div class="min-w-0">
                <div class="flex items-center gap-2">
                  <UserPlus class="size-4 text-muted-foreground" />
                  <span class="truncate font-bold text-sm">{{ member.username }}</span>
                  <Badge v-if="member.id === currentUserId" class="h-5 min-w-5 rounded-full px-1 font-mono tabular-nums transition-colors bg-green-100 text-green-700 border-green-200">You</Badge>
                  <Badge :variant="member.isActive ? 'outline' : 'destructive'">
                    {{ member.isActive ? "Active" : "Disabled" }}
                  </Badge>
                </div>
                <p class="mt-1 truncate font-mono text-xs text-muted-foreground">
                  {{ member.mail }}
                </p>
              </div>
              <div v-if="member.id !== currentUserId" class="flex gap-1 opacity-0 transition-opacity group-hover:opacity-100">
                <Button variant="ghost" size="icon" class="h-8 w-8" title="Sign out all sessions" @click="revokeSessions(member)" v-permission="'edit'">
                  <LogOut class="size-4" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  class="h-8 w-8"
                  :title="member.isActive ? 'Disable user' : 'Enable user'"
                  @click="toggleStatus(member)"
                  v-permission="'edit'"
                >
                  <PowerOff v-if="member.isActive" class="size-4" />
                  <Power v-else class="size-4" />
                </Button>
                <Button variant="ghost" size="icon" class="h-8 w-8" @click="startEdit(member)" v-permission="'edit'">
                  <Edit2 class="size-4" />
                </Button>
                <Button variant="ghost" size="icon" class="h-8 w-8 text-destructive" @click="remove(member)" v-permission="'delete'">
                  <Trash2 class="size-4" />
                </Button>
              </div>
            </div>

            <div class="mt-auto border-t pt-3">
              <div class="mb-2 text-[0.65rem] uppercase tracking-wider text-muted-foreground">
                Roles
              </div>
              <div v-if="memberRoleNames(member).length" class="flex flex-wrap gap-1.5">
                <Badge v-for="name in memberRoleNames(member)" :key="name" variant="outline">
                  {{ name }}
                </Badge>
              </div>
              <div v-else class="text-xs text-muted-foreground">No roles assigned.</div>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
