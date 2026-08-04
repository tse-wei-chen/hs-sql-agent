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
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Checkbox } from '@/components/ui/checkbox'
import { Badge } from "@/components/ui/badge";
import { Edit2, Save, ShieldCheck, Trash2, X } from "@lucide/vue";
import FormField from "@/components/FormField.vue";
import {
  createRole,
  deleteRole,
  getRoleDependencies,
  listPermissionActionTemplates,
  listRoles,
  updateRole,
  type AuthAction,
  type Permission,
  type PermissionActionTemplate,
  type Role,
} from "~/api/role";
import { toast } from "vue-sonner"
import { useForm } from "vee-validate";

definePageMeta({
  layout: "default",
  permission: "/auth/role.view",
});

interface RoleFormValues {
  name: string;
  description: string;
}

const { meta, values, setValues, resetForm: resetVeeForm, handleSubmit } = useForm<RoleFormValues>({
  initialValues: {
    name: "",
    description: "",
  },
});

const roles = ref<Role[]>([]);
const permissionActionTemplates = ref<PermissionActionTemplate[]>([]);
const selectedPermissionActionTemplateIds = ref<number[]>([]);
const loading = ref(false);
const saving = ref(false);
const editingId = ref<number | null>(null);
const loadError = ref("");
const matrixKey = ref(0);

const permissionActionMap = computed(() => {
  const map = new Map<string, number>();
  for (const item of permissionActionTemplates.value) {
    map.set(`${item.permission.id}:${item.action.id}`, item.id);
  }
  return map;
});

const groupedPermissions = computed(() => {
  const map = new Map<number, Permission>();
  for (const item of permissionActionTemplates.value) {
    map.set(item.permission.id, item.permission);
  }

  return [...map.values()].sort((a, b) => a.path.localeCompare(b.path));
});

const actions = computed(() => {
  const map = new Map<number, AuthAction>();
  for (const item of permissionActionTemplates.value) {
    map.set(item.action.id, item.action);
  }

  return [...map.values()].sort((a, b) => a.code.localeCompare(b.code));
});

const togglePermissionAction = (permissionId: number, actionId: number, checked: boolean | "indeterminate") => {
  const id = permissionActionMap.value.get(`${permissionId}:${actionId}`);
  if (!id) return;

  if (checked === true) {
    if (!selectedPermissionActionTemplateIds.value.includes(id)) {
      selectedPermissionActionTemplateIds.value.push(id);
    }
    return;
  }

  selectedPermissionActionTemplateIds.value = selectedPermissionActionTemplateIds.value.filter((item) => item !== id);
};

const permissionActionLabel = (id: number) => {
  const item = permissionActionTemplates.value.find((x) => x.id === id);
  if (!item) return `#${id}`;

  return `${item.permission.path}:${item.action.code}`;
};

const permissionActionLabelBySelection = (permissionId: number, actionId: number) => {
  const template = permissionActionTemplates.value.find((x) => x.permission.id === permissionId && x.action.id === actionId);
  if (template) {
    return permissionActionLabel(template.id);
  }

  const permission = permissionActionTemplates.value.find((x) => x.permission.id === permissionId)?.permission;
  const action = actions.value.find((x) => x.id === actionId);
  return `${permission?.path ?? permissionId}:${action?.code ?? actionId}`;
};

const load = async () => {
  loading.value = true;
  loadError.value = "";
  try {
    const [roleData, permissionActionTemplateData] = await Promise.all([
      listRoles(),
      listPermissionActionTemplates(),
    ]);

    roles.value = roleData;
    permissionActionTemplates.value = permissionActionTemplateData;
  } catch (error: any) {
    loadError.value = error?.response?.data || "Failed to load auth settings.";
  } finally {
    loading.value = false;
  }
};

const resetForm = () => {
  resetVeeForm();
  selectedPermissionActionTemplateIds.value = [];
  editingId.value = null;
  matrixKey.value++;
};

const startEdit = (role: Role) => {
  editingId.value = role.id;
  setValues({
    name: role.name,
    description: role.description ?? "",
  });
  selectedPermissionActionTemplateIds.value = (role.permissionActions ?? [])
    .map((item) => {
      const template = permissionActionTemplates.value.find((t) => {
        return String(t.permission.id) === String(item.permissionId) &&
          String(t.action.id) === String(item.actionId);
      });
      return template ? template.id : null;
    })
    .filter((id): id is number => typeof id === "number");
  matrixKey.value++;

  window.scrollTo({ top: 0, behavior: "smooth" });
};

const save = async () => {
  saving.value = true;
  try {
    const payload = {
      name: values.name.trim(),
      description: values.description?.trim() || null,
      permissionActions: selectedPermissionActionTemplateIds.value
        .map((id) => permissionActionTemplates.value.find((item) => item.id === id))
        .filter((item): item is PermissionActionTemplate => !!item)
        .map((item) => ({
          permissionId: item.permission.id,
          actionId: item.action.id,
        })),
    };

    if (editingId.value) {
      await updateRole(editingId.value, payload);
    } else {
      await createRole(payload);
    }

    resetForm();
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to save role.");
  } finally {
    saving.value = false;
  }
};

const onSave = handleSubmit(save);

const remove = async (role: Role) => {
  try {
    const dependencies = await getRoleDependencies(role.id);
    const affected = dependencies.members;
    const accounts = affected.length ? affected.map((member) => `- ${member.mail}`).join("\n") : "- None";
    const permissions = dependencies.permissions.length ? dependencies.permissions.map((permission) => `- ${permission}`).join("\n") : "- None";
    const detail = `\n\nAffected accounts:\n${accounts}\n\nPermissions removed:\n${permissions}`;
    if (!confirm(`Delete role "${role.name}"?${detail}`)) return;
    await deleteRole(role.id, affected.length > 0);
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to delete role.");
  }
};

onMounted(load);
</script>

<template>
  <div class="space-y-6">
    <Card>
      <CardHeader class="border-b">
        <CardTitle>{{ editingId ? "Edit Role" : "Create Role" }}</CardTitle>
        <CardDescription>Assign route permissions and allowed actions to a role.</CardDescription>
      </CardHeader>
      <CardContent class="pt-6">
        <form class="space-y-6" @submit.prevent="onSave">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <FormField name="name" rules="required" label="Role Name">
              <template #default="{ field }">
                <Input v-bind="field" id="name" placeholder="e.g., Operator" />
              </template>
            </FormField>

            <FormField name="description" label="Description">
              <template #default="{ field }">
                <Textarea v-bind="field" id="description" placeholder="Describe this role" />
              </template>
            </FormField>
          </FieldGroup>

          <Field>
            <div class="mb-3 flex items-center justify-between">
              <FieldLabel>Permission Actions</FieldLabel>
              <span class="text-xs text-muted-foreground">{{ selectedPermissionActionTemplateIds.length }}
                selected</span>
            </div>

            <div v-if="loading" class="rounded-md border p-4 text-sm text-muted-foreground">
              Loading permission matrix...
            </div>
            <div v-else-if="loadError"
              class="rounded-md border border-destructive/30 bg-destructive/5 p-4 text-sm text-destructive">
              {{ loadError }}
            </div>
            <div v-else-if="!permissionActionTemplates.length"
              class="rounded-md border p-4 text-sm text-muted-foreground">
              No permissions or actions defined yet.
            </div>
            <div v-else class="overflow-x-auto rounded-md border">
              <table :key="matrixKey" class="w-full min-w-[720px] text-sm">
                <thead class="bg-muted/50">
                  <tr>
                    <th class="w-[280px] px-3 py-2 text-left font-medium">Route</th>
                    <th v-for="action in actions" :key="action.id" class="px-3 py-2 text-left font-medium">
                      {{ action.name }}
                    </th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="permission in groupedPermissions" :key="permission.id" class="border-t">
                    <td class="px-3 py-3">
                      <div class="font-medium">{{ permission.name }}</div>
                      <div class="font-mono text-xs text-muted-foreground">{{ permission.path }}</div>
                    </td>
                    <td v-for="action in actions" :key="action.id" class="px-3 py-3">
                      <Checkbox class="size-4" :disabled="!permissionActionMap.has(`${permission.id}:${action.id}`)"
                        v-model="computed({
                          get: () => selectedPermissionActionTemplateIds.includes(permissionActionMap.get(`${permission.id}:${action.id}`) ?? -1),
                          set: (val) => togglePermissionAction(permission.id, action.id, val)
                        }).value" />
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </Field>

          <div class="flex justify-end gap-2 pt-2">
            <Button v-if="editingId" type="button" variant="ghost" @click="resetForm">
              <X class="mr-2 size-4" /> Cancel
            </Button>
            <Button type="submit" :disabled="!meta.valid || saving" v-permission="[editingId ? 'edit' : 'create']">
              <Save v-if="!saving" class="mr-2 size-4" />
              {{ saving ? "Saving..." : editingId ? "Update Role" : "Create Role" }}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Registered Roles</CardTitle>
      </CardHeader>
      <CardContent class="pt-6">
        <div v-if="loading" class="py-8 text-center text-sm text-muted-foreground">
          Loading roles...
        </div>
        <div v-else-if="roles.length === 0" class="py-8 text-center text-sm text-muted-foreground">
          No roles defined yet.
        </div>
        <div v-else class="grid gap-4 lg:grid-cols-2">
          <div v-for="role in roles" :key="role.id"
            class="group flex flex-col rounded-lg border bg-card p-4 shadow-sm transition-colors hover:border-primary/50">
            <div class="mb-3 flex items-start justify-between gap-3">
              <div>
                <div class="flex items-center gap-2">
                  <ShieldCheck class="size-4 text-muted-foreground" />
                  <span class="font-bold text-sm">{{ role.name }}</span>
                  <Badge v-if="role.name === 'SuperUser'"
                    class="h-5 min-w-5 rounded-full px-1 font-mono tabular-nums transition-colors bg-green-100 text-green-700 border-green-200">
                    Built-in</Badge>
                </div>
                <p class="mt-1 text-xs text-muted-foreground">
                  {{ role.description || "No description" }}
                </p>
              </div>
              <div v-if="role.name !== 'SuperUser'"
                class="flex gap-1 opacity-0 transition-opacity group-hover:opacity-100">
                <Button variant="ghost" size="icon" class="h-8 w-8" @click="startEdit(role)" v-permission="'edit'">
                  <Edit2 class="size-4" />
                </Button>
                <Button variant="ghost" size="icon" class="h-8 w-8 text-destructive" @click="remove(role)"
                  v-permission="'delete'">
                  <Trash2 class="size-4" />
                </Button>
              </div>
            </div>

            <div class="mt-auto border-t pt-3">
              <div class="mb-2 text-[0.65rem] uppercase tracking-wider text-muted-foreground">
                Permission actions
              </div>
              <div v-if="role.permissionActions?.length" class="flex flex-wrap gap-1.5">
                <Badge v-for="item in role.permissionActions.slice(0, 8)" :key="`${item.permissionId}:${item.actionId}`"
                  variant="outline" class="font-mono text-[0.65rem]">
                  {{ permissionActionLabelBySelection(item.permissionId, item.actionId) }}
                </Badge>
                <Badge v-if="role.permissionActions.length > 8"
                  class="h-5 min-w-5 rounded-full px-1 font-mono tabular-nums transition-colors bg-green-100 text-green-700 border-green-200">
                  +{{ role.permissionActions.length - 8 }}
                </Badge>
              </div>
              <div v-else class="text-xs text-muted-foreground">No permission actions assigned.</div>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
