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
import { Textarea } from "@/components/ui/textarea";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import { CircleAlert, CircleCheck, KeyRound , CircleQuestionMark  } from "@lucide/vue";
import {
  issueMcpKey,
  cloneMcpKey,
  listMcpKeys,
  revokeMcpKey,
  rotateMcpKey,
  testDbConnection,
  updateMcpKey,
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
  createMcpOnboardingSnippets,
  getMcpEndpoint,
  allowedToolsRequireElicitation,
  type McpKeyDetail,
  type McpKeyRateLimitMode,
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
  isExpiringSoon: boolean;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  allowedTools?: string | null;
  corsAllowedOrigins?: string | null;
  sqlProvider?: string | null;
  dbManagementId?: number | null;
  dbManagementName?: string | null;
  tableWhitelist?: string | null;
  rateLimitMode: McpKeyRateLimitMode;
  permitLimitOverride?: number | null;
  windowSecondsOverride?: number | null;
  effectivePermitLimit?: number | null;
  effectiveWindowSeconds?: number | null;
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
const issuedKeyName = ref("");
const mcpEndpoint = ref("");
const lifecycleMode = ref<"edit" | "rotate" | "clone" | null>(null);
const lifecycleKey = ref<McpKeyItem | null>(null);
const lifecycleSaving = ref(false);
const lifecycleName = ref("");
const lifecycleExpiresAt = ref("");
const lifecycleAllowedTools = ref("");
const lifecycleCors = ref("");
const lifecycleDbManagementId = ref<number | null>(null);
const lifecycleTableWhitelist = ref("");
const lifecycleRateLimitMode = ref<McpKeyRateLimitMode>("Inherit");
const lifecyclePermitLimit = ref(120);
const lifecycleWindowSeconds = ref(60);
const gracePeriodMinutes = ref(0);

const expiringSoonKeys = computed(() =>
  keys.value.filter((key) => key.isExpiringSoon),
);

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

const dmlToolNames = computed(() => new Set(
  customTools.value.filter((tool) => tool.type === "DML").map((tool) => tool.name),
));

const issueRequiresElicitation = computed(() =>
  allowedToolsRequireElicitation(detail.value.allowedTools, dmlToolNames.value),
);
const lifecycleRequiresElicitation = computed(() => allowedToolsRequireElicitation(
  lifecycleAllowedTools.value.split(",").map((name) => name.trim()).filter(Boolean),
  dmlToolNames.value,
));

const onboardingSnippets = computed(() =>
  createMcpOnboardingSnippets(mcpEndpoint.value, issuedPlaintextKey.value),
);

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
  issuedKeyName.value = ""
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
      rateLimitMode: detail.value.rateLimitMode,
      permitLimitOverride:
        detail.value.rateLimitMode === "Custom"
          ? detail.value.permitLimitOverride
          : null,
      windowSecondsOverride:
        detail.value.rateLimitMode === "Custom"
          ? detail.value.windowSecondsOverride
          : null,
    });

    resetForm()
    await load();
    issuedPlaintextKey.value = result.plaintextKey || "";
    issuedKeyName.value = result.name || values.name;
  } catch (error: any) {
    toast.error(
      error?.response?.data?.error ||
        error?.response?.data ||
        error?.message ||
        "Failed to issue MCP key.",
    );
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

const toLocalDateTime = (value?: string | null) => {
  if (!value) return "";
  const date = new Date(value);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
};

const lifecycleExpiry = () =>
  lifecycleExpiresAt.value
    ? new Date(lifecycleExpiresAt.value).toISOString()
    : null;

const openLifecycle = (
  mode: "edit" | "rotate" | "clone",
  key: McpKeyItem,
) => {
  lifecycleMode.value = mode;
  lifecycleKey.value = key;
  lifecycleName.value = mode === "clone" ? `${key.name} Copy` : key.name;
  const reusableExpiry =
    key.expiresAt && new Date(key.expiresAt) > new Date()
      ? key.expiresAt
      : new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString();
  lifecycleExpiresAt.value = toLocalDateTime(
    mode === "edit" ? key.expiresAt : reusableExpiry,
  );
  lifecycleAllowedTools.value = key.allowedTools || "";
  lifecycleCors.value = key.corsAllowedOrigins || "";
  lifecycleDbManagementId.value = key.dbManagementId ?? null;
  lifecycleTableWhitelist.value = key.tableWhitelist || "";
  lifecycleRateLimitMode.value = key.rateLimitMode || "Inherit";
  lifecyclePermitLimit.value = key.permitLimitOverride || key.effectivePermitLimit || 120;
  lifecycleWindowSeconds.value = key.windowSecondsOverride || key.effectiveWindowSeconds || 60;
  gracePeriodMinutes.value = 0;
};

const saveLifecycle = async () => {
  if (!lifecycleKey.value || !lifecycleMode.value) return;
  lifecycleSaving.value = true;
  try {
    let result;
    if (lifecycleMode.value === "edit") {
      result = await updateMcpKey(lifecycleKey.value.id, {
        name: lifecycleName.value.trim(),
        expiresAt: lifecycleExpiry(),
        allowedTools: lifecycleAllowedTools.value.trim() || null,
        corsAllowedOrigins: lifecycleCors.value.trim() || null,
        dbManagementId: lifecycleDbManagementId.value,
        tableWhitelist: lifecycleTableWhitelist.value.trim() || null,
        rateLimitMode: lifecycleRateLimitMode.value,
        permitLimitOverride:
          lifecycleRateLimitMode.value === "Custom"
            ? lifecyclePermitLimit.value
            : null,
        windowSecondsOverride:
          lifecycleRateLimitMode.value === "Custom"
            ? lifecycleWindowSeconds.value
            : null,
      });
      toast.success("MCP key settings updated.");
    } else if (lifecycleMode.value === "rotate") {
      result = await rotateMcpKey(lifecycleKey.value.id, {
        gracePeriodMinutes: gracePeriodMinutes.value,
        expiresAt: lifecycleExpiry(),
      });
      issuedPlaintextKey.value = result.plaintextKey || "";
      issuedKeyName.value = result.name || lifecycleKey.value.name;
      toast.success("MCP key rotated. Save the replacement key now.");
    } else {
      result = await cloneMcpKey(lifecycleKey.value.id, {
        name: lifecycleName.value.trim(),
        expiresAt: lifecycleExpiry(),
      });
      issuedPlaintextKey.value = result.plaintextKey || "";
      issuedKeyName.value = result.name || lifecycleName.value;
      toast.success("MCP key duplicated. Save the new key now.");
    }
    lifecycleMode.value = null;
    await load();
  } catch (error: any) {
    toast.error(
      error?.response?.data?.error ||
        error?.response?.data ||
        "MCP key lifecycle operation failed.",
    );
  } finally {
    lifecycleSaving.value = false;
  }
};

const copyIssuedKey = async () => {
  if (!issuedPlaintextKey.value) return;
  await navigator.clipboard.writeText(issuedPlaintextKey.value);
  toast.success("Key copied to clipboard.");
};

const copySnippet = async (value: string) => {
  await navigator.clipboard.writeText(value);
  toast.success("Configuration copied to clipboard.");
};

const closeOnboarding = () => {
  issuedPlaintextKey.value = "";
  issuedKeyName.value = "";
};

onMounted(async () => {
  mcpEndpoint.value = getMcpEndpoint(window.location.origin);
  await load();
});
</script>

<template>
  <div class="space-y-4">
    <div
      v-if="expiringSoonKeys.length"
      class="rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900"
    >
      <div class="font-medium">
        {{ expiringSoonKeys.length }} active key(s) expire within 7 days
      </div>
      <div>{{ expiringSoonKeys.map((key) => key.name).join(", ") }}</div>
    </div>

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
            <Field>
              <FieldLabel for="rateLimitMode">Per-key rate limit</FieldLabel>
              <Select v-model="detail.rateLimitMode">
                <SelectTrigger id="rateLimitMode" class="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Inherit">Use Security default</SelectItem>
                  <SelectItem value="Custom">Custom quota</SelectItem>
                  <SelectItem value="Unlimited">Unlimited</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <div v-if="detail.rateLimitMode === 'Custom'" class="grid grid-cols-2 gap-3">
              <Field>
                <FieldLabel for="permitLimitOverride">Requests</FieldLabel>
                <Input id="permitLimitOverride" v-model.number="detail.permitLimitOverride" type="number" min="1" max="1000000" />
              </Field>
              <Field>
                <FieldLabel for="windowSecondsOverride">Window (seconds)</FieldLabel>
                <Input id="windowSecondsOverride" v-model.number="detail.windowSecondsOverride" type="number" min="1" max="86400" />
              </Field>
            </div>
            <div
              v-else-if="detail.rateLimitMode === 'Unlimited'"
              class="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900"
            >
              Disables only this key's request quota. IP throttling, SQL concurrency,
              row limits, timeouts, and DML safeguards still apply.
            </div>
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
              <div
                v-if="issueRequiresElicitation"
                class="mt-2 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900"
              >
                This key can invoke DML. Its MCP client must support form Elicitation so a human can approve each commit; unsupported clients will be refused. An unrestricted tool list also includes DML.
              </div>
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
                (detail.expiresAt === 'custom' && !customExpiresAt) ||
                (isWhitelistEnabled && detail.tableWhitelist.length === 0) ||
                (detail.rateLimitMode === 'Custom' &&
                  (detail.permitLimitOverride < 1 || detail.windowSecondsOverride < 1))
              "
              class="w-full md:w-auto"
              v-permission="'create'"
            >
              <KeyRound />
              {{ issuing ? "Issuing..." : "Issue Key" }}
            </Button>
          </span>
        </form>

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
              <div class="text-muted-foreground">
                Allowed Tools: {{ key.allowedTools || "All" }}
              </div>
              <div class="text-muted-foreground">
                Rate Limit:
                <template v-if="key.rateLimitMode === 'Unlimited'">
                  Unlimited (per-key limit disabled)
                </template>
                <template v-else>
                  {{ key.effectivePermitLimit }} requests / {{ key.effectiveWindowSeconds }}s
                  ({{ key.rateLimitMode === "Custom" ? "key override" : "Security default" }})
                </template>
              </div>
              <div
                v-if="key.isExpiringSoon"
                class="mt-1 font-medium text-amber-600"
              >
                Expires within 7 days
              </div>
            </div>
            <div class="flex flex-wrap gap-2">
              <Button
                variant="outline"
                @click="openLifecycle('edit', key)"
                v-permission="'edit'"
              >Edit</Button>
              <Button
                variant="outline"
                @click="openLifecycle('clone', key)"
                v-permission="'create'"
              >Duplicate</Button>
              <Button
                variant="outline"
                :disabled="!key.isActive"
                @click="openLifecycle('rotate', key)"
                v-permission="'edit'"
              >Rotate</Button>
              <Button
                variant="destructive"
                :disabled="!key.isActive"
                @click="revoke(key.id)"
                v-permission="'revoke'"
                >Revoke</Button
              >
            </div>
          </div>
        </div>
      </CardContent>
    </Card>

    <Dialog :open="lifecycleMode !== null" @update:open="(open) => { if (!open) lifecycleMode = null }">
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            {{ lifecycleMode === "edit" ? "Edit MCP Key" : lifecycleMode === "rotate" ? "Rotate MCP Key" : "Duplicate MCP Key" }}
          </DialogTitle>
          <DialogDescription v-if="lifecycleMode === 'rotate'">
            A new secret is shown once. The old key is revoked immediately unless a grace period is set.
          </DialogDescription>
          <DialogDescription v-else-if="lifecycleMode === 'clone'">
            Copies database, tool, CORS, and table restrictions into a new key.
          </DialogDescription>
          <DialogDescription v-else>
            Changes take effect immediately and invalidate cached authorization.
          </DialogDescription>
        </DialogHeader>

        <div class="grid gap-4">
          <Field v-if="lifecycleMode !== 'rotate'">
            <FieldLabel>Name</FieldLabel>
            <Input v-model="lifecycleName" />
          </Field>
          <Field>
            <FieldLabel>Expires at</FieldLabel>
            <Input v-model="lifecycleExpiresAt" type="datetime-local" />
          </Field>
          <Field v-if="lifecycleMode === 'rotate'">
            <FieldLabel>Old key grace period (minutes)</FieldLabel>
            <Input v-model.number="gracePeriodMinutes" type="number" min="0" max="1440" />
          </Field>
          <template v-if="lifecycleMode === 'edit'">
            <Field>
              <FieldLabel>Database</FieldLabel>
              <Select v-model="lifecycleDbManagementId">
                <SelectTrigger class="w-full">
                  <SelectValue placeholder="Select database connection" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem v-for="db in dbManagements" :key="db.id" :value="db.id">
                    {{ db.name }} ({{ db.sqlProvider }})
                  </SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <Field>
              <FieldLabel>Allowed tools</FieldLabel>
              <Input v-model="lifecycleAllowedTools" placeholder="Comma-separated tool names" />
              <div
                v-if="lifecycleRequiresElicitation"
                class="mt-2 rounded-md border border-amber-300 bg-amber-50 p-3 text-xs text-amber-900"
              >
                DML is enabled (an empty list means all tools). The client must support MCP form Elicitation.
              </div>
            </Field>
            <Field>
              <FieldLabel>Table whitelist</FieldLabel>
              <Input v-model="lifecycleTableWhitelist" placeholder="schema.table, schema.table" />
            </Field>
            <Field>
              <FieldLabel>CORS allowed origins</FieldLabel>
              <Input v-model="lifecycleCors" placeholder="https://app.example.com" />
            </Field>
            <Field>
              <FieldLabel>Per-key rate limit</FieldLabel>
              <Select v-model="lifecycleRateLimitMode">
                <SelectTrigger class="w-full"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="Inherit">Use Security default</SelectItem>
                  <SelectItem value="Custom">Custom quota</SelectItem>
                  <SelectItem value="Unlimited">Unlimited</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <div v-if="lifecycleRateLimitMode === 'Custom'" class="grid grid-cols-2 gap-3">
              <Field>
                <FieldLabel>Requests</FieldLabel>
                <Input v-model.number="lifecyclePermitLimit" type="number" min="1" max="1000000" />
              </Field>
              <Field>
                <FieldLabel>Window (seconds)</FieldLabel>
                <Input v-model.number="lifecycleWindowSeconds" type="number" min="1" max="86400" />
              </Field>
            </div>
            <div
              v-else-if="lifecycleRateLimitMode === 'Unlimited'"
              class="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900"
            >
              Only the per-key request quota is disabled; other safety controls remain active.
            </div>
          </template>
        </div>

        <DialogFooter>
          <Button variant="outline" @click="lifecycleMode = null">Cancel</Button>
          <Button
            :disabled="
              lifecycleSaving ||
              (lifecycleMode !== 'rotate' && !lifecycleName.trim()) ||
              (lifecycleMode === 'rotate' && (gracePeriodMinutes < 0 || gracePeriodMinutes > 1440)) ||
              (lifecycleMode === 'edit' && lifecycleRateLimitMode === 'Custom' &&
                (lifecyclePermitLimit < 1 || lifecycleWindowSeconds < 1))
            "
            @click="saveLifecycle"
          >
            {{ lifecycleSaving ? "Saving..." : lifecycleMode === "edit" ? "Save changes" : lifecycleMode === "rotate" ? "Rotate key" : "Duplicate key" }}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <Dialog :open="Boolean(issuedPlaintextKey)" @update:open="(open) => { if (!open) closeOnboarding() }">
      <DialogContent class="max-h-[90vh] overflow-y-auto sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Save and connect {{ issuedKeyName || "this MCP key" }}</DialogTitle>
          <DialogDescription>
            This secret and every generated configuration are available only in this dialog. Closing it permanently removes the plaintext value from the Admin UI.
          </DialogDescription>
        </DialogHeader>

        <div class="space-y-4">
          <div class="rounded border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
            Treat copied snippets as secrets. Do not commit them to source control or paste them into tickets.
          </div>
          <Field>
            <FieldLabel>One-time key value</FieldLabel>
            <div class="break-all rounded border bg-muted/40 p-3 font-mono text-xs">{{ issuedPlaintextKey }}</div>
            <Button class="mt-2" size="sm" variant="outline" @click="copyIssuedKey">Copy key</Button>
          </Field>
          <Field>
            <FieldLabel>MCP endpoint</FieldLabel>
            <Input v-model="mcpEndpoint" />
          </Field>

          <Tabs default-value="claude">
            <TabsList>
              <TabsTrigger value="claude">Claude Desktop</TabsTrigger>
              <TabsTrigger value="cursor">Cursor</TabsTrigger>
              <TabsTrigger value="generic">Generic HTTP</TabsTrigger>
            </TabsList>
            <TabsContent value="claude" class="space-y-2">
              <p class="text-xs text-muted-foreground">
                Add this direct HTTP entry to Claude Desktop's MCP configuration. It connects without a local Node.js bridge.
              </p>
              <Textarea :model-value="onboardingSnippets.claudeDesktop" readonly class="min-h-48 font-mono text-xs" />
              <Button size="sm" variant="outline" @click="copySnippet(onboardingSnippets.claudeDesktop)">Copy Claude config</Button>
            </TabsContent>
            <TabsContent value="cursor" class="space-y-2">
              <p class="text-xs text-muted-foreground">Add this entry to Cursor's MCP configuration.</p>
              <Textarea :model-value="onboardingSnippets.cursor" readonly class="min-h-48 font-mono text-xs" />
              <Button size="sm" variant="outline" @click="copySnippet(onboardingSnippets.cursor)">Copy Cursor config</Button>
            </TabsContent>
            <TabsContent value="generic" class="space-y-2">
              <p class="text-xs text-muted-foreground">Generic Streamable HTTP client connection object.</p>
              <Textarea :model-value="onboardingSnippets.genericHttp" readonly class="min-h-40 font-mono text-xs" />
              <Button size="sm" variant="outline" @click="copySnippet(onboardingSnippets.genericHttp)">Copy HTTP config</Button>
            </TabsContent>
          </Tabs>

        </div>

        <DialogFooter>
          <Button variant="destructive" @click="closeOnboarding">I saved it — close and forget secret</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </div>
</template>
