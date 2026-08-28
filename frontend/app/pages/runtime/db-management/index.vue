<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
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
import {
  Field,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { CircleAlert, CircleCheck,
  Eye,
  EyeOff,
  Database,
  Trash2,
  Edit2,
  Save,
  Library } from "@lucide/vue";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import {
  listDbManagements,
  createDbManagement,
  updateDbManagement,
  deleteDbManagement,
  type DbManagement,
} from "@/api/db-management";

import { PROVIDER_OPTIONS } from "~/constants/providerOptions";
import PasswordInput from "@/components/PasswordInput.vue";
import { testDbConnection } from "~/api/runtime";
import FormField from "@/components/FormField.vue";
import { toast } from "vue-sonner"
import { useForm } from "vee-validate";
import {
  buildDbConnectionTestRequest,
  requiresDbPassword,
} from "@/lib/db-management";

definePageMeta({
  layout: "default",
  permission: "/runtime/db-management.view",
});

interface DbFormValues {
  name: string
  sqlProvider: string
  host: string
  port: string
  username: string
  password: string
  database: string
  TrustServerCertificate: boolean
  Encrypt: boolean
}

const { meta, values, setValues, setFieldValue, resetForm: resetVeeForm, handleSubmit } = useForm<DbFormValues>({
  initialValues: {
    name: "",
    sqlProvider: "Mysql",
    host: "",
    port: "",
    username: "",
    password: "",
    database: "",
    TrustServerCertificate: false,
    Encrypt: false,
  },
})

const dbs = ref<DbManagement[]>([]);
const loading = ref(false);
const saving = ref(false);
const editingId = ref<number | null>(null);
const testing = ref(false);
const passwordRequired = computed(
  () => !editingId.value && requiresDbPassword(values.sqlProvider),
);
const testsSavedConnection = computed(
  () => editingId.value !== null && !values.password.trim(),
);

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
  resetVeeForm()
  editingId.value = null;
  connectionTestResult.value = null;
};

const startEdit = (db: DbManagement) => {
  connectionTestResult.value = null;
  editingId.value = db.id;
  const extraSettings = db.extraSettings ? JSON.parse(db.extraSettings) : {}
  setValues({
    name: db.name,
    sqlProvider: db.sqlProvider || "Mysql",
    host: db.host || "",
    port: db.port || "",
    username: db.username || "",
    password: "",
    database: db.database || "",
    TrustServerCertificate: extraSettings.TrustServerCertificate ?? false,
    Encrypt: extraSettings.Encrypt ?? false,
  })
  window.scrollTo({ top: 0, behavior: "smooth" });
};

const save = async () => {
  saving.value = true;
  try {
    const v = values
    const payload = {
      name: v.name,
      sqlProvider: v.sqlProvider,
      host: v.host,
      port: v.port,
      username: v.username,
      password: v.password,
      database: v.database,
      extraSettings: JSON.stringify({ TrustServerCertificate: v.TrustServerCertificate, Encrypt: v.Encrypt }),
    };

    if (editingId.value) {
      await updateDbManagement(editingId.value, payload);
    } else {
      await createDbManagement(payload);
    }

    resetForm();
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to save DB connection.");
  } finally {
    saving.value = false;
  }
};
const onSave = handleSubmit(save)

const remove = async (id: number) => {
  if (!confirm("Are you sure you want to delete this database connection?"))
    return;

  try {
    await deleteDbManagement(id);
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to delete DB connection.");
  }
};

const test = async () => {
  try {
    testing.value = true;
    connectionTestResult.value = null;
    const result = await testDbConnection(
      buildDbConnectionTestRequest(values, editingId.value),
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

watch(values, () => {
  connectionTestResult.value = null;
}, { deep: true });

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
        <form class="space-y-6" @submit.prevent="onSave">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <FormField name="name" rules="required" label="Connection Name" class="md:col-span-2">
              <template #default="{ field }">
                <Input v-bind="field" id="name" placeholder="e.g., Production DB" />
              </template>
            </FormField>

            <VeeField name="sqlProvider" rules="required" v-slot="{ field, errorMessage, meta: fieldMeta }">
              <Field>
                <FieldLabel for="sqlProvider">SQL Provider<RequiredStar /></FieldLabel>
                <div class="relative">
                  <Select
                    :model-value="field.value"
                    @update:model-value="field.onChange"
                  >
                    <SelectTrigger class="w-full">
                      <SelectValue placeholder="Select provider" />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem
                        v-for="[key, value] of PROVIDER_OPTIONS"
                        :key="key"
                        :value="key"
                      >
                        {{ value }}
                      </SelectItem>
                    </SelectContent>
                  </Select>
                  <TooltipProvider v-if="errorMessage && fieldMeta.touched">
                    <Tooltip>
                      <TooltipTrigger as-child>
                        <div class="absolute right-0 top-1/2 -translate-y-1/2 pr-3">
                          <CircleAlert class="size-4 text-destructive" />
                        </div>
                      </TooltipTrigger>
                      <TooltipContent side="top" align="end">
                        {{ errorMessage }}
                      </TooltipContent>
                    </Tooltip>
                  </TooltipProvider>
                  <div
                    v-else-if="fieldMeta.touched && fieldMeta.valid"
                    class="absolute right-0 top-1/2 -translate-y-1/2 pr-3"
                  >
                    <CircleCheck class="size-4 text-green-500" />
                  </div>
                </div>
              </Field>
            </VeeField>

            <template v-if="['Postgres', 'MySQL', 'MsSqlServer', 'Oracle', 'Firebird'].includes(values.sqlProvider)">
              <FormField name="host" label="Host" rightAddon>
                <template #default="{ field }">
                  <PasswordInput v-bind="field" id="host" placeholder="e.g., localhost or 192.168.1.100" />
                </template>
              </FormField>

              <FormField name="port" label="Port" rules="numeric" rightAddon>
                <template #default="{ field }">
                  <PasswordInput v-bind="field" id="port" placeholder="e.g., 3306" />
                </template>
              </FormField>

              <FormField name="username" label="Username" rightAddon>
                <template #default="{ field }">
                  <PasswordInput v-bind="field" id="username" placeholder="Database user" />
                </template>
              </FormField>

              <FormField
                name="password"
                :rules="passwordRequired ? 'required' : undefined"
                label="Password"
                rightAddon
                :helpText="editingId ? 'Leave blank to keep the saved password. Enter a value only to replace it.' : undefined"
              >
                <template #default="{ field }">
                  <PasswordInput v-bind="field" id="password" placeholder="Database password" />
                </template>
              </FormField>
            </template>

            <FormField
              v-if="['Sqlite', 'Postgres', 'MySQL', 'MsSqlServer', 'Oracle', 'Firebird'].includes(values.sqlProvider)"
              name="database" label="Database" rightAddon>
              <template #default="{ field }">
                <PasswordInput v-bind="field" id="database" placeholder="e.g., my_app_db" />
              </template>
            </FormField>

            <Field v-if="values.sqlProvider === 'MsSqlServer'" class="md:col-span-2">
              <FieldLabel>Extra Settings</FieldLabel>
              <div class="flex flex-wrap gap-6 border rounded-md p-4">
                <div class="flex items-center space-x-2">
                  <Checkbox
                    id="trustServerCertificate"
                    :checked="values.TrustServerCertificate"
                    @update:checked="(v: boolean) => setFieldValue('TrustServerCertificate', v)"
                  />
                  <Label
                    for="trustServerCertificate"
                    class="text-sm font-medium leading-none cursor-pointer"
                  >
                    Trust Server Certificate
                  </Label>
                </div>
                <div class="flex items-center space-x-2">
                  <Checkbox
                    id="encrypt"
                    :checked="values.Encrypt"
                    @update:checked="(v: boolean) => setFieldValue('Encrypt', v)"
                  />
                  <Label
                    for="encrypt"
                    class="text-sm font-medium leading-none cursor-pointer"
                  >
                    Encrypt Connection
                  </Label>
                </div>
              </div>
            </Field>
          </FieldGroup>

          <div class="flex justify-end gap-2 pt-4">
            <p
              v-if="testsSavedConnection"
              class="mr-auto self-center text-xs text-muted-foreground"
            >
              With the password blank, this tests the currently saved connection.
              Enter the password to test edited connection values.
            </p>
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
                      testing
                        ? "Connecting..."
                        : testsSavedConnection
                          ? "Test Saved Connection"
                          : "Test DB Connection"
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
            <Button type="submit" :disabled="!meta.valid || saving" v-permission="[editingId ? 'edit' : 'create']">
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
        <div v-else class="grid gap-4 lg:grid-cols-2">
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

                  <p
                    class="text-xs text-muted-foreground mt-1 font-mono items-center flex gap-1"
                  >
                    <template v-if="db.visible">
                      {{
                        db.host
                          ? `${db.host}${db.port ? ":" + db.port : ""}`
                          : "No host defined"
                      }}
                      <span v-if="db.database"> | {{ db.database }}</span>
                    </template>
                    <template v-else> ∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗∗ </template>
                    <button
                      @click="db.visible = !db.visible"
                      class="p-1 hover:bg-muted rounded-md transition-colors text-muted-foreground"
                      title="show/hide details"
                      tabindex= -1
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
                  class="h-8 w-8 text-blue-600"
                  title="Semantic Layer"
                  as-child
                >
                  <NuxtLink :to="`/runtime/db-management/${db.id}/semantic`"
                    ><Library class="size-4"
                  /></NuxtLink>
                </Button>
                <Button
                  v-if="!db.isBootstrapManaged"
                  variant="ghost"
                  size="icon"
                  class="h-8 w-8"
                  @click="startEdit(db)"
                  v-permission="'edit'"
                >
                  <Edit2 class="size-4" />
                </Button>
                <Button
                  v-if="!db.isBootstrapManaged"
                  variant="ghost"
                  size="icon"
                  class="h-8 w-8 text-destructive"
                  @click="remove(db.id)"
                  v-permission="'delete'"
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
