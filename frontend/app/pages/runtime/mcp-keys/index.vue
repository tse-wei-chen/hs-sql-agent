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
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Input } from "@/components/ui/input";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { CircleAlert, CircleCheck, KeyRound , CircleQuestionMark  } from "@lucide/vue";
import {
  issueMcpKey,
  listMcpKeys,
  revokeMcpKey,
  testDbConnection,
} from "@/api/runtime";
import { Switch } from "@/components/ui/switch";
import { listCustomSqlTools, type CustomSqlTool } from "@/api/custom-tools";
import {
  listDbManagements,
  getSchemas,
  getTables,
  type DbManagement,
} from "~/api/db-management";

import MultiSelect from "~/components/MultiSelect.vue";
import Transfer from "~/components/Transfer.vue";


import FormField from "@/components/FormField.vue";
import { toast } from "vue-sonner"
import { useForm } from "vee-validate";
import {
  createInitialMcpKeyDetail,
  formatAllowedToolsLabel,
  resolveMcpKeyExpiry,
  serializeTableWhitelist,
  type McpKeyDetail,
} from "@/lib/mcpKeyIssuance";

definePageMeta({
  layout: "default",
  permission: "/runtime/mcp-keys.view",
});

interface McpKeyItem {
  id: number;
  name: string;
  keyPrefix: string;
  isActive: boolean;
  isExpired: boolean;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  corsAllowedOrigins?: string | null;
  sqlProvider?: string | null;
  dbManagementId?: number | null;
  dbManagementName?: string | null;
  tableWhitelist?: string | null;
}

const { meta, values, setFieldValue, resetForm: resetVeeForm, handleSubmit } = useForm<{ name: string; dbManagementId: number | null }>({
  initialValues: { name: "", dbManagementId: null },
})

const keys = ref<McpKeyItem[]>([]);
const customTools = ref<CustomSqlTool[]>([]);
const dbManagements = ref<DbManagement[]>([]);
const loading = ref(false);
const issuing = ref(false);
const testing = ref(false);
const customExpiresAt = ref("");
const detail = ref<McpKeyDetail>(createInitialMcpKeyDetail());
const isWhitelistEnabled = ref(false);
const selectedSchema = ref<string | undefined>(undefined);
const availableSchemas = ref<string[]>([]);
const fetchingSchemas = ref(false);
const availableTables = ref<string[]>([]);
const fetchingTables = ref(false);
const connectionTestResult = ref<{
  success: boolean;
  errorMessage: string;
} | null>(null);
const issuedPlaintextKey = ref("");

watch(
  () => detail.value.dbManagementId,
  async (newVal) => {
    selectedSchema.value = undefined;
    availableTables.value = [];
    detail.value.tableWhitelist = [];
    if (newVal) {
      fetchingSchemas.value = true;
      try {
        availableSchemas.value = await getSchemas(newVal);
        if (availableSchemas.value.length > 0) {
          selectedSchema.value = availableSchemas.value[0];
        }
      } catch {
        availableSchemas.value = [];
      } finally {
        fetchingSchemas.value = false;
      }
    } else {
      availableSchemas.value = [];
    }
  },
);

watch(
  () => selectedSchema.value,
  async (newVal) => {
    if (newVal && detail.value.dbManagementId) {
      fetchingTables.value = true;
      try {
        availableTables.value = await getTables(
          detail.value.dbManagementId,
          newVal,
        );
      } catch {
        availableTables.value = [];
      } finally {
        fetchingTables.value = false;
      }
    } else {
      availableTables.value = [];
    }
  },
);

const tableOptions = computed(() => {
  return availableTables.value.map((t) => ({
    label: t,
    value: `${selectedSchema.value}.${t}`,
  }));
});

const baseToolOptions = [
  { label: "Execute Query", value: "execute_query_sql", risk: "medium" },
  { label: "Get Columns", value: "get_columns", risk: "low" },
  { label: "Get Schemas", value: "get_schemas", risk: "low" },
  { label: "Get Tables", value: "get_tables", risk: "low" },
  { label: "Execute DML", value: "execute_dml_sql", risk: "high" },
];

const toolOptions = computed(() => {
  const customOptions = customTools.value.map((t) => ({
    label: `Custom: ${t.name}`,
    value: t.name,
    risk: t.type === "DML" ? "high" : "medium",
  }));
  return [...baseToolOptions, ...customOptions];
});

const selectedToolLabel = computed(() => {
  return formatAllowedToolsLabel(detail.value.allowedTools);
});

const load = async () => {
  loading.value = true;
  try {
    const [keysResult, customToolsResult, dbManagementsResult] =
      await Promise.all([
        listMcpKeys(),
        listCustomSqlTools(),
        listDbManagements(),
      ]);
    keys.value = keysResult;
    customTools.value = customToolsResult;
    dbManagements.value = dbManagementsResult;
  } finally {
    loading.value = false;
  }
};

const resetForm = () => {
  resetVeeForm()
  detail.value = createInitialMcpKeyDetail()
  customExpiresAt.value = ""
  isWhitelistEnabled.value = false
  selectedSchema.value = undefined
  issuedPlaintextKey.value = ""
};

const issue = async () => {
  try {
    const expiresAt = resolveMcpKeyExpiry(
      detail.value.expiresAt,
      customExpiresAt.value,
    );
    const tableWhitelist = serializeTableWhitelist(
      isWhitelistEnabled.value,
      detail.value.tableWhitelist,
    );
    issuing.value = true;
    const result = await issueMcpKey({
      name: values.name.trim(),
      expiresAt,
      allowedTools:
        detail.value.allowedTools?.length > 0
          ? detail.value.allowedTools.join(",")
          : null,
      corsAllowedOrigins: detail.value.corsAllowedOrigins?.trim() || null,
      dbManagementId: detail.value.dbManagementId || 0,
      tableWhitelist,
    });

    resetForm()
    await load();
    issuedPlaintextKey.value = result.plaintextKey || "";
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to issue MCP key.");
  } finally {
    issuing.value = false;
  }
};
const onIssue = handleSubmit(issue)

const test = async () => {
  try {
    testing.value = true;
    connectionTestResult.value = null;
    const result = await testDbConnection({
      dbSettingMode: 0,
      dbManagementId: detail.value.dbManagementId ?? undefined,
    });
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

const revoke = async (id: number) => {
  try {
    await revokeMcpKey(id);
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to revoke key.");
  }
};

onMounted(load);
</script>

<template>
  <div class="space-y-4">
    <Card>
      <CardHeader class="border-b">
        <CardTitle>Issue MCP Access Key</CardTitle>
        <CardDescription>
          New keys are shown only once. Save the value immediately.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form class="space-y-6 pt-4" @submit.prevent="onIssue">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <FormField name="name" rules="required" label="Name">
              <template #default="{ field }">
                <Input v-bind="field" id="name" placeholder="Claude Desktop Production" />
              </template>
            </FormField>

            <Field>
              <FieldLabel for="expiresMode">Expires</FieldLabel>
              <Select v-model="detail.expiresAt">
                <SelectTrigger id="expiresMode" class="w-full">
                  <SelectValue placeholder="Select expiry" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem :value="null">Never</SelectItem>
                  <SelectItem :value="1">1 day</SelectItem>
                  <SelectItem :value="7">7 days</SelectItem>
                  <SelectItem :value="30">30 days</SelectItem>
                  <SelectItem :value="'custom'">Custom date/time</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <span class="md:col-span-2">
              <hr />
            </span>
            <Field v-if="detail.expiresAt === 'custom'" class="md:col-span-2">
              <FieldLabel for="customExpiresAt">Custom Expires At</FieldLabel>
              <Input
                id="customExpiresAt"
                v-model="customExpiresAt"
                type="datetime-local"
              />
            </Field>
            <VeeField name="dbManagementId" rules="required" v-slot="{ errorMessage, meta: fieldMeta }">
              <Field>
                <FieldLabel>Database<RequiredStar /></FieldLabel>
                <div class="relative">
                  <Select :modelValue="detail.dbManagementId" @update:modelValue="(v: unknown) => { detail.dbManagementId = v as number | null; setFieldValue('dbManagementId', v as number | null) }">
                    <SelectTrigger class="w-full">
                      <SelectValue placeholder="Select database connection" />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem
                        v-for="db in dbManagements"
                        :key="db.id"
                        :value="db.id"
                      >
                        {{ db.name }} ({{ db.sqlProvider }})
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
            <span class="md:col-span-2">
              <hr />
            </span>
            <Field class="md:col-span-2">
              <div class="flex items-center justify-start gap-2">
                <FieldLabel>Restrict Data Access (Advanced) </FieldLabel>
                <TooltipProvider>
                  <Tooltip>
                    <TooltipTrigger as-child>
                      <CircleQuestionMark
                        class="h-5 w-5 text-muted-foreground"
                      />
                    </TooltipTrigger>
                    <TooltipContent>
                      <p class="mt-1 text-xs text-background">
                        If enabled, you can restrict the tables that the AI can
                        access.
                      </p>
                    </TooltipContent>
                  </Tooltip>
                </TooltipProvider>
                <Switch id="enable-whitelist" v-model="isWhitelistEnabled" />
              </div>
              <div
                v-if="!detail.dbManagementId && isWhitelistEnabled"
                class="py-12 border-2 border-dashed rounded-lg text-center text-muted-foreground"
              >
                Please select a database connection first.
              </div>
              <div
                v-else-if="fetchingSchemas && isWhitelistEnabled"
                class="py-12 border-2 border-dashed rounded-lg text-center text-muted-foreground"
              >
                Fetching schemas...
              </div>
              <Tabs
                v-else-if="isWhitelistEnabled"
                v-model="selectedSchema"
                class="w-full"
              >
                <div class="flex items-center gap-4 mb-4">
                  <div class="flex-1 overflow-x-auto pb-2">
                    <TabsList
                      class="inline-flex h-9 items-center justify-start rounded-lg bg-muted p-1 text-muted-foreground w-auto min-w-full"
                    >
                      <TabsTrigger
                        v-for="s in availableSchemas"
                        :key="s"
                        :value="s"
                        class="inline-flex items-center justify-center whitespace-nowrap rounded-md px-3 py-1 text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50 data-[state=active]:bg-background data-[state=active]:text-foreground data-[state=active]:shadow"
                      >
                        {{ s }}
                      </TabsTrigger>
                    </TabsList>
                  </div>
                </div>

                <div
                  v-if="!selectedSchema"
                  class="py-20 border rounded-lg bg-muted/20 text-center text-muted-foreground"
                >
                  Select a schema above to manage table whitelist.
                </div>
                <div v-else class="space-y-4">
                  <div
                    v-if="fetchingTables"
                    class="py-20 border rounded-lg bg-muted/20 text-center text-muted-foreground"
                  >
                    Fetching tables for {{ selectedSchema }}...
                  </div>
                  <Transfer
                    v-else
                    v-model="detail.tableWhitelist"
                    :options="tableOptions"
                    :disabled="fetchingTables"
                    :left-title="`Tables in ${selectedSchema}`"
                    right-title="Whitelist"
                  />
                  <p class="text-xs text-muted-foreground">
                    Selected tables:
                    {{
                      detail.tableWhitelist.length ||
                      "None — select at least one table"
                    }}
                  </p>
                </div>
              </Tabs>
            </Field>
            <span class="md:col-span-2">
              <hr />
            </span>
            <Field class="md:col-span-2">
              <FieldLabel>Allowed Tools (multi-select)</FieldLabel>
              <MultiSelect
                v-model="detail.allowedTools"
                :options="toolOptions"
                :placeholder="selectedToolLabel"
              >
                <template #option="{ option }">
                  <div class="flex items-center justify-between w-full">
                    <span class="truncate pr-2">{{ option.label }}</span>
                    <span
                      class="text-xs font-mono shrink-0"
                      :class="{
                        'text-red-500': option.risk === 'high',
                        'text-yellow-500': option.risk === 'medium',
                        'text-emerald-500': option.risk === 'low',
                      }"
                    >
                      {{ option.value }}
                    </span>
                  </div>
                </template>
              </MultiSelect>
            </Field>
            <Field class="md:col-span-2">
              <div class="flex items-center justify-start gap-2">
                <FieldLabel for="corsAllowedOrigins"
                  >CORS Allowed Origins</FieldLabel
                >
                <TooltipProvider>
                  <Tooltip>
                    <TooltipTrigger as-child>
                      <CircleQuestionMark
                        class="h-5 w-5 text-muted-foreground"
                      />
                    </TooltipTrigger>
                    <TooltipContent>
                      <p class="mt-1 text-xs text-background">
                        Comma-separated origins. Leave empty to block browser
                        cross-origin requests for this key.
                      </p>
                    </TooltipContent>
                  </Tooltip>
                </TooltipProvider>
              </div>
              <Input
                id="corsAllowedOrigins"
                v-model="detail.corsAllowedOrigins"
                placeholder="https://app.example.com, https://admin.example.com"
              />
            </Field>
          </FieldGroup>
          <span class="item-center flex justify-end gap-2">
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
            <Button
              type="submit"
              :disabled="
                !meta.valid ||
                issuing ||
                (isWhitelistEnabled && detail.tableWhitelist.length === 0)
              "
              class="w-full md:w-auto"
              v-permission="'create'"
            >
              <KeyRound />
              {{ issuing ? "Issuing..." : "Issue Key" }}
            </Button>
          </span>
        </form>

        <div
          v-if="issuedPlaintextKey"
          class="mt-4 rounded border border-border bg-muted/40 p-3 text-sm"
        >
          <div class="font-medium">One-time key value</div>
          <div class="mt-1 break-all">{{ issuedPlaintextKey }}</div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Issued Keys</CardTitle>
      </CardHeader>
      <CardContent>
        <div v-if="loading" class="py-8 text-sm text-muted-foreground">
          Loading keys...
        </div>
        <div
          v-else-if="keys.length === 0"
          class="py-8 text-sm text-muted-foreground"
        >
          No issued keys yet.
        </div>
        <div v-else class="space-y-2 pt-4">
          <div
            v-for="key in keys"
            :key="key.id"
            class="flex flex-col gap-3 rounded-lg border bg-card p-4 shadow-sm md:flex-row md:items-center md:justify-between"
          >
            <div class="text-sm">
              <div class="font-medium">{{ key.name }}</div>
              <div class="text-muted-foreground">
                Prefix: {{ key.keyPrefix }} | Status:
                {{ key.isExpired ? "expired" : key.isActive ? "active" : "revoked" }}
              </div>
              <div class="text-muted-foreground">
                Expires: {{ key.expiresAt || "never" }}
              </div>
              <div class="text-muted-foreground">
                Last used: {{ key.lastUsedAt || "never" }}
              </div>
              <div class="text-muted-foreground">
                CORS: {{ key.corsAllowedOrigins || "none" }}
              </div>
              <div class="text-muted-foreground">
                Database:
                {{ key.dbManagementName || (key.dbManagementId ? `Missing connection #${key.dbManagementId}` : "none") }}
                <template v-if="key.sqlProvider"> ({{ key.sqlProvider }})</template>
              </div>
              <div class="text-muted-foreground">
                Table Whitelist: {{ key.tableWhitelist || "All" }}
              </div>
            </div>
            <Button
              variant="destructive"
              :disabled="!key.isActive"
              @click="revoke(key.id)"
              v-permission="'revoke'"
              >Revoke</Button
            >
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
