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
  listCustomSqlTools,
  createCustomSqlTool,
  updateCustomSqlTool,
  deleteCustomSqlTool,
  type CustomSqlTool,
} from "@/api/custom-tools";
import {
  listDbManagements,
  getSchemas,
  getTables,
  getColumns,
  type DbManagement,
} from "@/api/db-management";
import { Plus, Trash2, Edit2, Save, X, Wand2 } from "lucide-vue-next";
import { json } from "@codemirror/lang-json";
import { oneDark } from '@codemirror/theme-one-dark';
definePageMeta({
  layout: "default",
});
const tools = ref<CustomSqlTool[]>([]);
const loading = ref(false);
const saving = ref(false);
const editingId = ref<number | null>(null);
const colorMode = useColorMode()
// Form state
const form = ref({
  name: "",
  description: "",
  type: "Query" as "Query" | "DML",
  definitionJson: "",
  parameters: [] as { name: string; type: string; description: string }[],
});

// Assistant State
const dbs = ref<DbManagement[]>([]);

const loadDbs = async () => {
  try {
    dbs.value = await listDbManagements();
  } catch (e) {
    console.error("Failed to load DBs", e);
  }
};

const isAdvancedBuilderOpen = ref(false);

const onAdvancedBuilderApply = (json: string) => {
  form.value.definitionJson = json;
  // Trigger format
  formatJson();
};

const isJsonValid = computed(() => {
  if (!form.value.definitionJson) return true;
  try {
    JSON.parse(form.value.definitionJson);
    return true;
  } catch {
    return false;
  }
});

const formatJson = () => {
  if (isJsonValid.value && form.value.definitionJson) {
    try {
      const parsed = JSON.parse(form.value.definitionJson);
      form.value.definitionJson = JSON.stringify(parsed, null, 2);
    } catch { }
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
  form.value = {
    name: "",
    description: "",
    type: "Query",
    definitionJson: "",
    parameters: [],
  };
  editingId.value = null;
};

const addParameter = () => {
  form.value.parameters.push({ name: "", type: "string", description: "" });
};

const removeParameter = (index: number) => {
  form.value.parameters.splice(index, 1);
};

const startEdit = (tool: CustomSqlTool) => {
  editingId.value = tool.id;
  form.value = {
    name: tool.name,
    description: tool.description,
    type: tool.type,
    definitionJson: tool.definitionJson,
    parameters: tool.parametersJson ? JSON.parse(tool.parametersJson) : [],
  };
  // Scroll to form or show dialog (in this case, we'll just show the form at top)
  window.scrollTo({ top: 0, behavior: "smooth" });
};

const save = async () => {
  if (
    !form.value.name ||
    !form.value.description ||
    !form.value.definitionJson
  ) {
    alert("Please fill in all required fields.");
    return;
  }

  // Validate JSON
  try {
    JSON.parse(form.value.definitionJson);
  } catch (e) {
    alert("Invalid JSON in Definition.");
    return;
  }

  saving.value = true;
  try {
    const payload = {
      ...form.value,
      parametersJson: JSON.stringify(form.value.parameters),
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
    alert(error?.response?.data || "Failed to save tool.");
  } finally {
    saving.value = false;
  }
};

const remove = async (id: number) => {
  if (!confirm("Are you sure you want to delete this tool?")) return;

  try {
    await deleteCustomSqlTool(id);
    await load();
  } catch (error: any) {
    alert(error?.response?.data || "Failed to delete tool.");
  }
};

watch(
  () => form.value.type,
  (newVal, oldVal) => {
    if (newVal !== oldVal) {
      form.value.definitionJson = "";
      form.value.parameters = [];
      isAdvancedBuilderOpen.value = false;
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
      <CardHeader class="border-b ">
        <CardTitle>{{
          editingId ? "Edit Custom Tool" : "Create Custom Tool"
        }}</CardTitle>
        <CardDescription>
          Define a new SQL tool that will be exposed to the AI agent.
        </CardDescription>
      </CardHeader>
      <CardContent class="pt-6">
        <form class="space-y-6" @submit.prevent="save">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <Field>
              <FieldLabel for="name">Tool Name</FieldLabel>
              <Input id="name" v-model="form.name" placeholder="e.g., get_vip_customers" />
              <p class="text-[0.7rem] text-muted-foreground mt-1">
                Snake case recommended. This is how the LLM will see it.
              </p>
            </Field>

            <Field>
              <FieldLabel for="type">Operation Type</FieldLabel>
              <Select v-model="form.type">
                <SelectTrigger id="type">
                  <SelectValue placeholder="Select type" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Query">Query (SELECT)</SelectItem>
                  <SelectItem value="DML">DML (INSERT/UPDATE/DELETE)</SelectItem>
                </SelectContent>
              </Select>
            </Field>

            <Field class="md:col-span-2">
              <FieldLabel for="description">Description (for LLM)</FieldLabel>
              <Textarea id="description" v-model="form.description"
                placeholder="Describe what this tool does and when to use it..." />
            </Field>

            <Field class="md:col-span-2">
              <div class="flex items-center justify-between mb-2">
                <FieldLabel for="definition" class="mb-0">SQL Definition (JSON)</FieldLabel>
                <div class="flex items-center gap-2">
                  <Button v-if="form.type === 'Query'" variant="default" size="sm"
                    class="h-7 text-xs bg-indigo-600 hover:bg-indigo-700 text-white"
                    @click="isAdvancedBuilderOpen = true" type="button">
                    <Wand2 class="size-3 mr-1" /> Open Advanced Builder
                  </Button>
                  <Button variant="outline" size="sm" class="h-7 text-[0.65rem] px-2" @click="formatJson" type="button"
                    :disabled="!isJsonValid || !form.definitionJson">
                    Format JSON
                  </Button>
                </div>
              </div>

              <div class="space-y-2">
                <div
                  class="border rounded-md overflow-hidden focus-within:ring-1 focus-within:ring-primary focus-within:border-primary transition-shadow">
                  <NuxtCodeMirror :key="colorMode.value" id="definition" :editable="true" :extensions="[json()]"
                    :theme="colorMode.value === 'dark' ? oneDark : undefined" :basic-setup="true"
                    :indent-with-tab="true" v-model="form.definitionJson" :style="{
                      minHeight: '150px',
                      maxHeight: '400px',
                      overflowY: 'auto',
                    }" placeholder='{ "tableName": "customers", "selectColumns": [{ "field": "name" }] }' />
                </div>
                <div class="flex justify-between items-start">
                  <p class="text-[0.7rem] text-muted-foreground" v-pre>
                    Use {{ parameterName }} as placeholders in values.
                  </p>
                  <p v-if="!isJsonValid" class="text-[0.7rem] text-destructive font-semibold">
                    Invalid JSON format
                  </p>
                </div>
              </div>
            </Field>
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

            <div v-if="form.parameters.length === 0"
              class="text-xs text-muted-foreground border rounded-lg p-4 bg-muted/20 text-center">
              No parameters defined yet.
            </div>

            <div v-for="(p, index) in form.parameters" :key="index"
              class="flex items-start gap-3 p-3 border rounded-lg bg-muted/5 group">
              
              <div class="grid grid-cols-12 gap-3 flex-1">
                <Field class="col-span-3">
                  <FieldLabel class="text-[0.65rem] uppercase tracking-wider text-muted-foreground">Name</FieldLabel>
                  <Input v-model="p.name" placeholder="eg. id"  />
                </Field>

                <Field class="col-span-3">
                  <FieldLabel class="text-[0.65rem] uppercase tracking-wider text-muted-foreground">Type</FieldLabel>
                  <Select v-model="p.type">
                    <SelectTrigger >
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
                  <Input v-model="p.description" placeholder="eg. User unique ID"/>
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
            <Button type="submit" :disabled="saving">
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
      <CardHeader class="border-b ">
        <CardTitle>Registered Custom Tools</CardTitle>
      </CardHeader>
      <CardContent class="pt-6">
        <div v-if="loading" class="py-8 text-sm text-muted-foreground text-center">
          Loading tools...
        </div>
        <div v-else-if="tools.length === 0" class="py-8 text-sm text-muted-foreground text-center">
          No custom tools defined yet.
        </div>
        <div v-else class="grid gap-4 md:grid-cols-2">
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
                <Button variant="ghost" size="icon" class="h-8 w-8" @click="startEdit(tool)">
                  <Edit2 class="size-4" />
                </Button>
                <Button variant="ghost" size="icon" class="h-8 w-8 text-destructive" @click="remove(tool.id)">
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

    <AdvancedJsonBuilderDialog v-model:open="isAdvancedBuilderOpen" :type="form.type as any" :dbs="dbs"
      @apply="onAdvancedBuilderApply" />
  </div>
</template>
