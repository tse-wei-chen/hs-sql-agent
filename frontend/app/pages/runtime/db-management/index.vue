<script setup lang="ts">
import { onMounted, ref } from "vue";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Input } from "@/components/ui/input";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import {
  listDbManagements,
  createDbManagement,
  updateDbManagement,
  deleteDbManagement,
  type DbManagement,
} from "@/api/db-management";
import { Eye, EyeOff, Database, Trash2, Edit2, Save } from "lucide-vue-next";
import { PROVIDER_OPTIONS } from "~/constants/providerOptions";
import PasswordInput from "@/components/PasswordInput.vue";
import { testDbConnection } from "~/api/runtime";

definePageMeta({
  layout: "default",
});

const dbs = ref<DbManagement[]>([]);
const loading = ref(false);
const saving = ref(false);
const editingId = ref<number | null>(null);
const testing = ref(false);
// Form state
const form = ref({
  name: "",
  sqlProvider: "Mysql",
  host: "",
  port: "",
  username: "",
  password: "",
  database: "",
});

const connectionTestResult = ref<{
  success: boolean;
  errorMessage: string;
} | null>(null);

const load = async () => {
  loading.value = true;
  try {
    dbs.value = (await listDbManagements()).map((db) => ({
      ...db,
      visible: false,
    }));
  } finally {
    loading.value = false;
  }
};

const resetForm = () => {
  form.value = {
    name: "",
    sqlProvider: "Mysql",
    host: "",
    port: "",
    username: "",
    password: "",
    database: "",
  };
  editingId.value = null;
};

const startEdit = (db: DbManagement) => {
  editingId.value = db.id;
  form.value = {
    name: db.name,
    sqlProvider: db.sqlProvider || "Mysql",
    host: db.host || "",
    port: db.port || "",
    username: db.username || "",
    password: "", // do not fill password on edit for security, let them supply new if changed
    database: db.database || "",
  };
  window.scrollTo({ top: 0, behavior: "smooth" });
};

const save = async () => {
  if (!form.value.name || !form.value.sqlProvider) {
    alert("Name and SQL Provider are required.");
    return;
  }

  saving.value = true;
  try {
    const payload = { ...form.value };
    // Clean up empty strings just in case

    if (editingId.value) {
      // If password is empty on edit, we usually don't update it in backend, assuming backend handles this gracefully
      await updateDbManagement(editingId.value, payload);
    } else {
      await createDbManagement(payload);
    }

    resetForm();
    await load();
  } catch (error: any) {
    alert(error?.response?.data || "Failed to save DB connection.");
  } finally {
    saving.value = false;
  }
};

const remove = async (id: number) => {
  if (!confirm("Are you sure you want to delete this database connection?"))
    return;

  try {
    await deleteDbManagement(id);
    await load();
  } catch (error: any) {
    alert(error?.response?.data || "Failed to delete DB connection.");
  }
};

const test = async () => {
  try {
    testing.value = true;
    connectionTestResult.value = null;
    const result = await testDbConnection(
      1,
      undefined,
      form.value.sqlProvider ?? undefined,
      form.value.host ?? undefined,
      form.value.port ?? undefined,
      form.value.username ?? undefined,
      form.value.password ?? undefined,
      form.value.database ?? undefined,
    );
    connectionTestResult.value = {
      success: result.success,
      errorMessage: result.errorMessage || "Connection failed.",
    };
  } catch (error: any) {
    connectionTestResult.value = {
      success: false,
      errorMessage: error?.response?.data || "Connection failed.",
    };
  } finally {
    testing.value = false;
  }
};

onMounted(load);
</script>

<template>
  <div class="space-y-6">
    <!-- DB Connection Editor -->
    <Card>
      <CardHeader class="border-b">
        <CardTitle>{{
          editingId ? "Edit Database Connection" : "Create Database Connection"
        }}</CardTitle>
        <CardDescription>
          Configure database connection details.
        </CardDescription>
      </CardHeader>
      <CardContent class="pt-6">
        <form class="space-y-6" @submit.prevent="save">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <Field class="md:col-span-2">
              <FieldLabel for="name">Connection Name *</FieldLabel>
              <Input
                id="name"
                v-model="form.name"
                placeholder="e.g., Production DB"
                required
              />
            </Field>

            <Field>
              <FieldLabel for="sqlProvider">SQL Provider *</FieldLabel>
              <Select v-model="form.sqlProvider">
                <SelectTrigger class="w-full">
                  <SelectValue placeholder="Select provider" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem
                    v-for="provider in PROVIDER_OPTIONS"
                    :key="provider"
                    :value="provider"
                  >
                    {{ provider }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </Field>

            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(form.sqlProvider)
              "
            >
              <FieldLabel for="host">Host</FieldLabel>
              <PasswordInput
                id="host"
                v-model="form.host"
                placeholder="e.g., localhost or 192.168.1.100"
              />
            </Field>

            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(form.sqlProvider)
              "
            >
              <FieldLabel for="port">Port</FieldLabel>
              <PasswordInput
                id="port"
                v-model="form.port"
                placeholder="e.g., 3306"
              />
            </Field>

            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(form.sqlProvider)
              "
            >
              <FieldLabel for="username">Username</FieldLabel>
              <PasswordInput
                id="username"
                v-model="form.username"
                placeholder="Database user"
              />
            </Field>

            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(form.sqlProvider)
              "
            >
              <FieldLabel for="password">Password</FieldLabel>
              <PasswordInput
                id="password"
                v-model="form.password"
                placeholder="Database password"
              />
              <p
                v-if="editingId"
                class="text-[0.7rem] text-muted-foreground mt-1"
              >
                Leave blank to keep existing password intact.
              </p>
            </Field>

            <Field
              v-if="
                [
                  'Sqlite',
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(form.sqlProvider)
              "
            >
              <FieldLabel for="database">Database</FieldLabel>
              <PasswordInput
                id="database"
                v-model="form.database"
                placeholder="e.g., my_app_db"
              />
            </Field>
          </FieldGroup>

          <div class="flex justify-end gap-2 pt-4">
            <Button
              v-if="editingId"
              type="button"
              variant="ghost"
              @click="resetForm"
            >
              Cancel
            </Button>
            <TooltipProvider>
              <Tooltip
                :disabled="
                  !connectionTestResult || connectionTestResult.success === true
                "
              >
                <TooltipTrigger as-child>
                  <Button
                    type="button"
                    variant="outline"
                    :disabled="testing"
                    class="w-full md:w-auto flex items-center gap-2"
                    @click.prevent="test"
                  >
                    <Badge
                      :class="[
                        'h-5 min-w-5 rounded-full px-1 font-mono tabular-nums transition-colors',
                        !connectionTestResult
                          ? 'bg-slate-100 text-slate-500'
                          : connectionTestResult.success === true
                            ? 'bg-green-100 text-green-700 border-green-200'
                            : connectionTestResult.success === false
                              ? 'bg-red-100 text-red-700 border-red-200'
                              : 'bg-slate-100 text-slate-500',
                      ]"
                    >
                      <template
                        v-if="
                          connectionTestResult &&
                          connectionTestResult.success === true
                        "
                      >
                        ✓
                      </template>
                      <template
                        v-else-if="
                          connectionTestResult &&
                          connectionTestResult.success === false
                        "
                      >
                        ✗
                      </template>
                      <template v-else>?</template>
                    </Badge>
                    <span>{{
                      testing ? "Connecting..." : "Test DB Connection"
                    }}</span>
                  </Button>
                </TooltipTrigger>
                <TooltipContent
                  class="max-w-[300px] bg-red-950 text-white border-none"
                >
                  <p class="font-mono text-xs">
                    {{ connectionTestResult?.errorMessage }}
                  </p>
                </TooltipContent>
              </Tooltip>
            </TooltipProvider>
            <Button type="submit" :disabled="saving">
              <Save v-if="!saving" class="size-4 mr-2" />
              {{
                saving
                  ? "Saving..."
                  : editingId
                    ? "Update Connection"
                    : "Create Connection"
              }}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>

    <!-- DB Connections List -->
    <Card>
      <CardHeader class="border-b">
        <CardTitle>Registered Database Connections</CardTitle>
      </CardHeader>
      <CardContent class="pt-6">
        <div
          v-if="loading"
          class="py-8 text-sm text-muted-foreground text-center"
        >
          Loading connections...
        </div>
        <div
          v-else-if="dbs.length === 0"
          class="py-8 text-sm text-muted-foreground text-center"
        >
          No database connections defined yet.
        </div>
        <div v-else class="grid gap-4 md:grid-cols-2">
          <div
            v-for="db in dbs"
            :key="db.id"
            class="flex flex-col rounded-lg border bg-card p-4 shadow-sm group hover:border-primary/50 transition-colors"
          >
            <div class="flex items-start justify-between mb-2">
              <div class="flex items-start gap-3">
                <div class="flex-1">
                  <div class="flex items-center gap-2">
                    <Database class="size-4 text-muted-foreground" />
                    <span class="font-bold text-sm">{{ db.name }}</span>
                    <span
                      class="px-1.5 py-0.5 rounded text-[0.6rem] font-bold uppercase tracking-wider bg-primary/10 text-primary"
                    >
                      {{ db.sqlProvider }}
                    </span>
                  </div>

                  <p class="text-xs text-muted-foreground mt-1 font-mono items-center flex gap-1">
                    <template v-if="db.visible">
                      {{
                        db.host
                          ? `${db.host}${db.port ? ":" + db.port : ""}`
                          : "No host defined"
                      }}
                      <span v-if="db.database"> | {{ db.database }}</span>
                    </template>
                    <template v-else>
                      ∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗
                    </template>
                    <button
                      @click="db.visible = !db.visible"
                      class="p-1 hover:bg-muted rounded-md transition-colors text-muted-foreground"
                      title="show/hide details"
                    >
                      <Eye v-if="!db.visible" class="size-4" />
                      <EyeOff v-else class="size-4" />
                    </button>
                  </p>
                </div>
              </div>
              <div
                class="flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity"
              >
                <Button
                  variant="ghost"
                  size="icon"
                  class="h-8 w-8"
                  @click="startEdit(db)"
                >
                  <Edit2 class="size-4" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  class="h-8 w-8 text-destructive"
                  @click="remove(db.id)"
                >
                  <Trash2 class="size-4" />
                </Button>
              </div>
            </div>

            <div
              class="mt-auto pt-3 border-t flex items-center justify-between text-[0.65rem] text-muted-foreground"
            >
              <span v-if="db.visible">User: {{ db.username || "None" }}</span>
              <span v-else>User: ∗∗∗∗∗∗∗</span>
              <span
                >Updated:
                {{
                  new Date(db.updatedAt || db.createdAt).toLocaleString()
                }}</span
              >
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
