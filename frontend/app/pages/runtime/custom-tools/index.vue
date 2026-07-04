<script setup lang="ts">
import { onMounted, ref, computed } from "vue";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { CircleAlert, CircleCheck, Plus, Trash2, Edit2, Save, X, Play } from "@lucide/vue";
import {
  listCustomSqlTools,
  createCustomSqlTool,
  updateCustomSqlTool,
  deleteCustomSqlTool,
  testExecuteCustomSqlTool,
  type CustomSqlTool,
  type TestExecuteResult,
} from "@/api/custom-tools";
import {
  listDbManagements,
  type DbManagement,
} from "@/api/db-management";

import { parseSqlCustomSqlTool } from "@/api/custom-tools";
import { json } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import FormField from "@/components/FormField.vue";
import { toast } from "vue-sonner"
import { useForm } from "vee-validate";

definePageMeta({
  layout: "default",
  permission: "/runtime/custom-tools.view",
});

interface ToolFormValues {
  name: string
  description: string
  type: 'Query' | 'DML'
  definitionJson: string
}

const { meta, values, setValues, setFieldValue, resetForm: resetVeeForm, handleSubmit } = useForm<ToolFormValues>({
  initialValues: {
    name: "",
    description: "",
    type: "Query",
    definitionJson: "",
  },
})

const tools = ref<CustomSqlTool[]>([]);
const loading = ref(false);
const saving = ref(false);
const editingId = ref<number | null>(null);
const colorMode = useColorMode();
const parameters = ref<{ name: string; type: string; description: string }[]>([]);

const dbs = ref<DbManagement[]>([]);

// Test Execute state
const isTestDialogOpen = ref(false);
const testDbId = ref<number | null>(null);
const testParamValues = ref<Record<string, string>>({});
const testExecuting = ref(false);
const testResult = ref<TestExecuteResult | null>(null);

const openTestDialog = () => {
  testDbId.value = dbs.value?.[0]?.id ?? null;
  testParamValues.value = {};
  testResult.value = null;
  isTestDialogOpen.value = true;
};

const runTestExecute = async () => {
  if (!testDbId.value) {
    toast.error("Please select a database.");
    return;
  }
  testExecuting.value = true;
  testResult.value = null;
  try {
    const result = await testExecuteCustomSqlTool({
      definitionJson: values.definitionJson,
      type: values.type,
      dbId: testDbId.value,
      parameters: Object.keys(testParamValues.value).length > 0 ? testParamValues.value : undefined,
    });
    testResult.value = result;
  } catch (error: any) {
    testResult.value = { success: false, error: getErrorMessage(error, "Failed to execute test.") };
  } finally {
    testExecuting.value = false;
  }
};

const getErrorMessage = (error: any, fallback: string) => {
  const data = error?.response?.data;
  if (!data) return fallback;
  if (typeof data === "string") return data;
  if (Array.isArray(data.errors)) {
    return [data.error, ...data.errors].filter(Boolean).join("\n");
  }
  if (data.error) return data.error;
  if (data.title) return data.title;
  return fallback;
};

const loadDbs = async () => {
  try {
    dbs.value = await listDbManagements();
  } catch (e: any) {
    toast.error(getErrorMessage(e, "Failed to load databases."));
  }
};

const isJsonValid = computed(() => {
  if (!values.definitionJson) return true;
  try {
    JSON.parse(values.definitionJson);
    return true;
  } catch {
    return false;
  }
});

const formatJson = () => {
  if (isJsonValid.value && values.definitionJson) {
    try {
      const parsed = JSON.parse(values.definitionJson);
      setFieldValue("definitionJson", JSON.stringify(parsed, null, 2));
    } catch {}
  }
};

const parseSql = async () => {
  if (!values.definitionJson) return;
  try {
    const result = await parseSqlCustomSqlTool(values.definitionJson);
    if (result.success) {
      setFieldValue("definitionJson", JSON.stringify(JSON.parse(result.data!), null, 2));
      toast.success("SQL parsed to QueryDefinition JSON");
    } else {
      toast.error(`Parse error: ${result.error}`);
    }
  } catch (e: any) {
    toast.error(`Parse error: ${e.message}`);
  }
};

const load = async () => {
  loading.value = true;
  try {
    tools.value = await listCustomSqlTools();
  } finally {
    loading.value = false;
  }
};

const resetForm = () => {
  resetVeeForm()
  parameters.value = []
  editingId.value = null;
};

const addParameter = () => {
  parameters.value.push({ name: "", type: "string", description: "" });
};

const removeParameter = (index: number) => {
  parameters.value.splice(index, 1);
};

const startEdit = (tool: CustomSqlTool) => {
  editingId.value = tool.id;
  setValues({
    name: tool.name,
    description: tool.description,
    type: tool.type,
    definitionJson: tool.definitionJson,
  })
  parameters.value = tool.parametersJson ? JSON.parse(tool.parametersJson) : [];
  window.scrollTo({ top: 0, behavior: "smooth" });
};

const save = async () => {
  if (parameters.value.some((p) => !p.name.trim())) {
    toast.error("All parameters must have a name.");
    return;
  }

  saving.value = true;
  try {
    const v = values
    const payload = {
      name: v.name,
      description: v.description,
      type: v.type,
      definitionJson: v.definitionJson,
      parametersJson: JSON.stringify(parameters.value),
    };

    if (editingId.value) {
      await updateCustomSqlTool(editingId.value, {
        ...payload,
        id: editingId.value,
      });
    } else {
      await createCustomSqlTool(payload);
    }

    resetForm();
    await load();
  } catch (error: any) {
    toast.error(getErrorMessage(error, "Failed to save tool."));
  } finally {
    saving.value = false;
  }
};
const onSave = handleSubmit(save)

const remove = async (id: number) => {
  if (!confirm("Are you sure you want to delete this tool?")) return;

  try {
    await deleteCustomSqlTool(id);
    await load();
  } catch (error: any) {
    toast.error(getErrorMessage(error, "Failed to delete tool."));
  }
};

watch(
  () => values.type,
  (newVal, oldVal) => {
    if (newVal !== oldVal) {
      setFieldValue("definitionJson", "");
      parameters.value = [];
    }
  },
);

onMounted(async () => {
  await load();
  await loadDbs();
});
</script>

<template>
  <div class="space-y-6">
    <!-- Tool Editor -->
    <Card>
      <CardHeader class="border-b">
        <CardTitle>{{
          editingId ? "Edit Custom Tool" : "Create Custom Tool"
        }}</CardTitle>
        <CardDescription>
          Define a new SQL tool that will be exposed to the AI agent.
        </CardDescription>
      </CardHeader>
      <CardContent class="pt-6">
        <form class="space-y-6" @submit.prevent="onSave">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <FormField name="name" rules="required" label="Tool Name" helpText="Snake case recommended. This is how the LLM will see it.">
              <template #default="{ field }">
                <Input v-bind="field" id="name" placeholder="e.g., get_vip_customers" />
              </template>
            </FormField>

            <Field>
              <FieldLabel for="type">Operation Type</FieldLabel>
              <Select :modelValue="values.type" @update:modelValue="(v: unknown) => setFieldValue('type', (v ?? 'Query') as 'Query' | 'DML')">
                <SelectTrigger id="type">
                  <SelectValue placeholder="Select type" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Query">Query (SELECT)</SelectItem>
                  <SelectItem value="DML">DML (INSERT/UPDATE/DELETE)</SelectItem>
                </SelectContent>
              </Select>
            </Field>

            <FormField name="description" rules="required" label="Description (for LLM)" class="md:col-span-2">
              <template #default="{ field }">
                <Textarea v-bind="field" id="description"
                  placeholder="Describe what this tool does and when to use it..." />
              </template>
            </FormField>

            <VeeField name="definitionJson" rules="required|json" v-slot="{ field, handleChange, errorMessage, meta: fieldMeta }">
              <Field class="md:col-span-2">
                <div class="flex items-center justify-between mb-2">
                  <FieldLabel for="definition" class="mb-0">SQL Definition (JSON)</FieldLabel>
                  <div class="flex items-center gap-2">
                    <Button v-if="values.type === 'Query'" variant="outline" size="sm"
                      class="h-7 text-[0.65rem] px-2" @click="parseSql" type="button"
                      :disabled="!values.definitionJson">
                      Parse SQL
                    </Button>
                    <Button variant="outline" size="sm" class="h-7 text-[0.65rem] px-2" @click="formatJson" type="button"
                      :disabled="!isJsonValid || !values.definitionJson">
                      Format JSON
                    </Button>
                    <Button variant="default" size="sm"
                      class="h-7 text-xs bg-emerald-600 hover:bg-emerald-700 text-white"
                      @click="openTestDialog" type="button"
                      :disabled="!isJsonValid || !values.definitionJson">
                      <Play class="size-3 mr-1" /> Test Execute
                    </Button>
                  </div>
                </div>

                <div class="space-y-2">
                  <div
                    class="border rounded-md overflow-hidden focus-within:ring-1 focus-within:ring-primary focus-within:border-primary transition-shadow">
                    <NuxtCodeMirror :key="colorMode.value" id="definition" :editable="true" :extensions="[json()]"
                      :theme="colorMode.value === 'dark' ? oneDark : undefined" :basic-setup="true"
                      :indent-with-tab="true" :modelValue="field.value" @update:modelValue="(v: string) => handleChange(v ?? '')"
                      :style="{
                        minHeight: '150px',
                        maxHeight: '400px',
                        overflowY: 'auto',
                      }" placeholder='{ "tableName": "customers", "selectColumns": [{ "field": "name" }] }' />
                  </div>
                  <div class="flex justify-between items-start">
                    <div class="space-y-1 text-[0.7rem] text-muted-foreground">
                      <p>
                        You can paste SQL here, then click Parse SQL to convert it into QueryDefinition JSON.
                      </p>
                      <p v-pre>
                        Use {{ parameterName }} as placeholders in values.
                      </p>
                    </div>
                    <TooltipProvider v-if="errorMessage && fieldMeta.touched">
                      <Tooltip>
                        <TooltipTrigger as-child>
                          <span class="cursor-default">
                            <CircleAlert class="size-4 text-destructive" />
                          </span>
                        </TooltipTrigger>
                        <TooltipContent side="top" align="end">
                          {{ errorMessage }}
                        </TooltipContent>
                      </Tooltip>
                    </TooltipProvider>
                    <CircleCheck
                      v-else-if="fieldMeta.touched && fieldMeta.valid"
                      class="size-4 text-green-500"
                    />
                  </div>
                </div>
              </Field>
            </VeeField>
          </FieldGroup>

          <!-- Parameters Section -->
          <div class="space-y-4">
            <div class="flex items-center justify-between">
              <h3 class="text-sm font-medium">
                Parameters (dynamic parameters decided by your AI)
              </h3>
              <Button type="button" variant="outline" size="sm" @click="addParameter">
                <Plus class="size-4 mr-1" /> Add Parameter
              </Button>
            </div>

            <div v-if="parameters.length === 0"
              class="text-xs text-muted-foreground border rounded-lg p-4 bg-muted/20 text-center">
              No parameters defined yet.
            </div>

            <div v-for="(p, index) in parameters" :key="index"
              class="flex items-start gap-3 p-3 border rounded-lg bg-muted/5 group">

              <div class="grid grid-cols-12 gap-3 flex-1">
                <Field class="col-span-3">
                  <FieldLabel class="text-[0.65rem] uppercase tracking-wider text-muted-foreground">Name</FieldLabel>
                  <Input v-model="p.name" placeholder="eg. id" />
                </Field>

                <Field class="col-span-3">
                  <FieldLabel class="text-[0.65rem] uppercase tracking-wider text-muted-foreground">Type</FieldLabel>
                  <Select v-model="p.type">
                    <SelectTrigger>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="string">string</SelectItem>
                      <SelectItem value="number">number</SelectItem>
                      <SelectItem value="boolean">boolean</SelectItem>
                    </SelectContent>
                  </Select>
                </Field>

                <Field class="col-span-6">
                  <FieldLabel class="text-[0.65rem] uppercase tracking-wider text-muted-foreground">Description</FieldLabel>
                  <Input v-model="p.description" placeholder="eg. User unique ID" />
                </Field>
              </div>

              <Button type="button" variant="outline" size="icon" @click="removeParameter(index)"
                class="mt-7 text-destructive">
                <X class="size-4" />
              </Button>
            </div>
          </div>

          <div class="flex justify-end gap-2 pt-4">
            <Button v-if="editingId" type="button" variant="ghost" @click="resetForm">
              Cancel
            </Button>
            <Button type="submit" :disabled="!meta.valid || saving" v-permission="[editingId ? 'edit' : 'create']">
              <Save v-if="!saving" class="size-4 mr-2" />
              {{
                saving ? "Saving..." : editingId ? "Update Tool" : "Create Tool"
              }}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>

    <!-- Tools List -->
    <Card>
      <CardHeader class="border-b">
        <CardTitle>Registered Custom Tools</CardTitle>
      </CardHeader>
      <CardContent class="pt-6">
        <div v-if="loading" class="py-8 text-sm text-muted-foreground text-center">
          Loading tools...
        </div>
        <div v-else-if="tools.length === 0" class="py-8 text-sm text-muted-foreground text-center">
          No custom tools defined yet.
        </div>
        <div v-else class="grid gap-4 lg:grid-cols-2">
          <div v-for="tool in tools" :key="tool.id"
            class="flex flex-col rounded-lg border bg-card p-4 shadow-sm group hover:border-primary/50 transition-colors">
            <div class="flex items-start justify-between mb-2">
              <div>
                <div class="flex items-center gap-2">
                  <span class="font-bold text-sm">{{ tool.name }}</span>
                  <span class="px-1.5 py-0.5 rounded text-[0.6rem] font-bold uppercase tracking-wider" :class="tool.type === 'DML'
                      ? 'bg-red-100 text-red-700'
                      : 'bg-blue-100 text-blue-700'
                    ">
                    {{ tool.type }}
                  </span>
                </div>
                <p class="text-xs text-muted-foreground line-clamp-2 mt-1">
                  {{ tool.description }}
                </p>
              </div>
              <div class="flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                <Button variant="ghost" size="icon" class="h-8 w-8" @click="startEdit(tool)" v-permission="'edit'">
                  <Edit2 class="size-4" />
                </Button>
                <Button variant="ghost" size="icon" class="h-8 w-8 text-destructive" @click="remove(tool.id)" v-permission="'delete'">
                  <Trash2 class="size-4" />
                </Button>
              </div>
            </div>

            <div class="mt-auto pt-3 border-t flex items-center justify-between text-[0.65rem] text-muted-foreground">
              <span>{{
                tool.parametersJson
                  ? JSON.parse(tool.parametersJson).length
                  : 0
              }}
                parameters</span>
              <span>Updated:
                {{
                  new Date(
                    tool.lastModifiedAt || tool.createdAt,
                  ).toLocaleString()
                }}</span>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Test Execute Dialog -->
    <Dialog v-model:open="isTestDialogOpen">
      <DialogContent class="sm:max-w-2xl max-h-[85vh] flex flex-col">
        <DialogHeader>
          <DialogTitle>Test Execute Tool</DialogTitle>
          <DialogDescription>
            Run this tool definition against a selected database with test parameters.
          </DialogDescription>
        </DialogHeader>

        <div class="flex-1 overflow-y-auto space-y-4 py-4">
          <Field>
            <FieldLabel>Target Database</FieldLabel>
            <Select v-model="testDbId">
              <SelectTrigger>
                <SelectValue placeholder="Select a database..." />
              </SelectTrigger>
              <SelectContent>
                <SelectItem v-for="db in dbs" :key="db.id" :value="db.id">
                  {{ db.name }}
                </SelectItem>
              </SelectContent>
            </Select>
          </Field>

          <div v-if="parameters.length > 0" class="space-y-3">
            <h4 class="text-sm font-medium">Parameters</h4>
            <div v-for="p in parameters" :key="p.name" class="grid grid-cols-4 gap-2 items-center">
              <span class="text-xs font-mono text-muted-foreground col-span-1">
                {{ '{' + '{' }}{{ p.name }}{{ '}' + '}' }}
              </span>
              <Input
                class="col-span-3 h-8 text-xs"
                :placeholder="`${p.description} (${p.type})`"
                :modelValue="testParamValues[p.name] ?? ''"
                @update:modelValue="(v: any) => { testParamValues[p.name] = v ?? '' }"
              />
            </div>
          </div>

          <div v-if="testResult" class="space-y-2">
            <h4 class="text-sm font-medium" :class="testResult.success ? 'text-green-600' : 'text-red-600'">
              {{ testResult.success ? 'Success' : 'Error' }}
            </h4>
            <pre class="border rounded-md p-3 text-xs bg-muted/20 overflow-auto max-h-60 whitespace-pre-wrap font-mono">{{ testResult.success ? testResult.data : testResult.error }}</pre>
          </div>
        </div>

        <DialogFooter class="border-t pt-4">
          <Button variant="outline" @click="isTestDialogOpen = false">Close</Button>
          <Button @click="runTestExecute" :disabled="testExecuting || !testDbId">
            {{ testExecuting ? "Executing..." : "Execute" }}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </div>
</template>
