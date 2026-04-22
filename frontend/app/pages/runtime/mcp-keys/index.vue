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
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Input } from "@/components/ui/input";
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field";
import {
  issueMcpKey,
  listMcpKeys,
  revokeMcpKey,
  testDbConnection,
} from "@/api/runtime";
import { listCustomSqlTools, type CustomSqlTool } from "@/api/custom-tools";
import PasswordInput from "@/components/PasswordInput.vue";
import { listDbManagements, type DbManagement } from "~/api/db-management";
import { RadioGroup, RadioGroupItem } from "~/components/ui/radio-group";
import { PROVIDER_OPTIONS } from "~/constants/providerOptions";

definePageMeta({
  layout: "default",
});

interface McpKeyItem {
  id: number;
  name: string;
  keyPrefix: string;
  isActive: boolean;
  lastUsedAt?: string | null;
  corsAllowedOrigins?: string | null;
  sqlProvider?: string | null;
  hasSqlConnectionStringOverride?: boolean;
}

const keys = ref<McpKeyItem[]>([]);
const customTools = ref<CustomSqlTool[]>([]);
const dbManagements = ref<DbManagement[]>([]);
const loading = ref(false);
const issuing = ref(false);
const testing = ref(false);
const customExpiresAt = ref("");
const selectedTools = ref<string[]>([]);
const detail = ref<{
  name: string;
  expiresAt: string | null;
  allowedTools: string[];
  corsAllowedOrigins: string;
  dbSettingMode: 0 | 1;
  dbManagementId: number | null;
  sqlProvider: string;
  host: string;
  port: string;
  username: string;
  password: string;
  database: string;
}>({
  name: "",
  expiresAt: null,
  allowedTools: [],
  corsAllowedOrigins: "",
  dbSettingMode: 0,
  dbManagementId: null,
  sqlProvider: "Global",
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

const issuedPlaintextKey = ref("");

const baseToolOptions = [
  { label: "Execute Query", value: "execute_query_safe", risk: "medium" },
  { label: "Get Columns", value: "get_columns", risk: "low" },
  { label: "Get Schemas", value: "get_schemas", risk: "low" },
  { label: "Get Tables", value: "get_tables", risk: "low" },
  { label: "Execute DML", value: "execute_dml_safe", risk: "high" },
];

const toolOptions = computed(() => {
  const customOptions = customTools.value.map((t) => ({
    label: `Tool: ${t.name}`,
    value: t.name,
    risk: t.type === "DML" ? "high" : "medium",
  }));
  return [...baseToolOptions, ...customOptions];
});

const selectedToolLabel = computed(() => {
  if (selectedTools.value.length === 0) {
    return "Global (no restriction)";
  }

  return `${selectedTools.value.length} tools selected`;
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

const issue = async () => {
  if (!detail.value.name.trim()) {
    alert("Key name is required.");
    return;
  }
  issuing.value = true;
  detail.value.expiresAt =
    detail.value.expiresAt === "custom"
      ? customExpiresAt.value
      : detail.value.expiresAt === null
        ? null
        : new Date(detail.value.expiresAt).toISOString();
  try {
    const result = await issueMcpKey({
      name: detail.value.name.trim(),
      expiresAt: detail.value.expiresAt,
      allowedTools:
        detail.value.allowedTools?.length > 0
          ? detail.value.allowedTools.join(",")
          : null,
      corsAllowedOrigins: detail.value.corsAllowedOrigins?.trim() || null,
      dbSettingMode: detail.value.dbSettingMode,
      dbManagementId: detail.value.dbManagementId || null,
      sqlProvider:
        detail.value.sqlProvider === "Global" ? null : detail.value.sqlProvider,
      host: detail.value.host.trim() || null,
      port: detail.value.port.trim() || null,
      username: detail.value.username.trim() || null,
      password: detail.value.password || null,
      database: detail.value.database.trim() || null,
    });

    issuedPlaintextKey.value = result.plaintextKey || "";
    detail.value = {
      name: "",
      expiresAt: null,
      allowedTools: [],
      corsAllowedOrigins: "",
      dbSettingMode: 0,
      dbManagementId: null,
      sqlProvider: "Global",
      host: "",
      port: "",
      username: "",
      password: "",
      database: "",
    };
    await load();
  } catch (error: any) {
    alert(error?.response?.data || "Failed to issue MCP key.");
  } finally {
    issuing.value = false;
  }
};

const test = async () => {
  try {
    testing.value = true;
    connectionTestResult.value = null;
    const result = await testDbConnection(
      detail.value.dbSettingMode,
      detail.value.dbManagementId ?? undefined,
      detail.value.sqlProvider ?? undefined,
      detail.value.host ?? undefined,
      detail.value.port ?? undefined,
      detail.value.username ?? undefined,
      detail.value.password ?? undefined,
      detail.value.database ?? undefined,
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

const revoke = async (id: number) => {
  try {
    await revokeMcpKey(id);
    await load();
  } catch (error: any) {
    alert(error?.response?.data || "Failed to revoke key.");
  }
};

watch(
  () => detail.value.sqlProvider,
  (newVal, oldVal) => {
    if (newVal !== oldVal) {
      detail.value.host = "";
      detail.value.port = "";
      detail.value.username = "";
      detail.value.password = "";
      detail.value.database = "";
    }
  },
);

watch(
  () => detail.value.dbSettingMode,
  (newVal, oldVal) => {
    if (newVal !== oldVal) {
      detail.value.dbManagementId = null;
      detail.value.sqlProvider = "Global";
      detail.value.host = "";
      detail.value.port = "";
      detail.value.username = "";
      detail.value.password = "";
      detail.value.database = "";
    }
  },
);

onMounted(load);
</script>

<template>
  <div class="space-y-4">
    <Card>
      <CardHeader class="border-b bg-muted/40">
        <CardTitle>Issue MCP Access Key</CardTitle>
        <CardDescription>
          New keys are shown only once. Save the value immediately.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form class="space-y-6 pt-4">
          <FieldGroup class="grid gap-4 md:grid-cols-2">
            <Field>
              <FieldLabel for="name">Name</FieldLabel>
              <Input
                id="name"
                v-model="detail.name"
                placeholder="Claude Desktop Production"
              />
            </Field>

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
              <FieldLabel>Db Setting</FieldLabel>
              <RadioGroup v-model="detail.dbSettingMode" class="flex flex-col">
                <div class="flex items-center space-x-2">
                  <RadioGroupItem id="r1" :value="0" />
                  <Label for="r1">Use Existing Connection</Label>
                </div>
                <div class="flex items-center space-x-2">
                  <RadioGroupItem id="r2" :value="1" />
                  <Label for="r2">Configure Manually</Label>
                </div>
              </RadioGroup>
            </Field>
            <Field v-if="detail.dbSettingMode === 0">
              <FieldLabel>Database Connection</FieldLabel>
              <Select v-model="detail.dbManagementId">
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
            </Field>
            <Field v-if="detail.dbSettingMode === 1">
              <FieldLabel>SQL Provider Override</FieldLabel>
              <Select v-model="detail.sqlProvider">
                <SelectTrigger class="w-full">
                  <SelectValue placeholder="Select provider" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem
                    v-for="provider in PROVIDER_OPTIONS"
                    :key="provider"
                    :value="provider"
                  >
                    {{ provider === "Global" ? "Global default" : provider }}
                  </SelectItem>
                </SelectContent>
              </Select>
              <p class="mt-1 text-xs text-muted-foreground">
                Use Global default, or select a provider together with a
                connection string.
              </p>
            </Field>

            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(detail.sqlProvider) && detail.dbSettingMode === 1
              "
            >
              <FieldLabel>Host</FieldLabel>
              <Input v-model="detail.host" placeholder="Host" />
            </Field>
            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(detail.sqlProvider) && detail.dbSettingMode === 1
              "
            >
              <FieldLabel>Port</FieldLabel>
              <Input v-model="detail.port" placeholder="Port" />
            </Field>
            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(detail.sqlProvider) && detail.dbSettingMode === 1
              "
            >
              <FieldLabel>Username</FieldLabel>
              <Input v-model="detail.username" placeholder="Username" />
            </Field>
            <Field
              v-if="
                [
                  'Postgres',
                  'MySQL',
                  'MsSqlServer',
                  'Oracle',
                  'Firebird',
                ].includes(detail.sqlProvider) && detail.dbSettingMode === 1
              "
            >
              <FieldLabel>Password</FieldLabel>
              <PasswordInput
                v-model="detail.password"
                type="password"
                placeholder="Password"
              />
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
                ].includes(detail.sqlProvider) && detail.dbSettingMode === 1
              "
            >
              <FieldLabel>Database</FieldLabel>
              <Input v-model="detail.database" placeholder="Database" />
            </Field>
            <span class="md:col-span-2">
              <hr />
            </span>
            <Field>
              <FieldLabel>Allowed Tools (multi-select)</FieldLabel>
              <Select v-model="detail.allowedTools" multiple>
                <SelectTrigger class="w-full">
                  <SelectValue :placeholder="selectedToolLabel" />
                </SelectTrigger>
                <SelectContent>
                  <SelectGroup>
                    <SelectLabel>Tools</SelectLabel>
                    <SelectItem
                      v-for="tool in toolOptions"
                      :key="tool.value"
                      :value="tool.value"
                    >
                      <div
                        class="flex w-full items-center justify-between gap-2"
                      >
                        <span>{{ tool.label }}</span>
                        <span
                          class="text-xs text-muted-foreground"
                          :class="{
                            'text-red-500': tool.risk === 'high',
                            'text-yellow-500': tool.risk === 'medium',
                            'text-emerald-500': tool.risk === 'low',
                          }"
                          >{{ tool.value }}</span
                        >
                      </div>
                    </SelectItem>
                  </SelectGroup>
                </SelectContent>
              </Select>
            </Field>
            <Field class="md:col-span-2">
              <FieldLabel for="corsAllowedOrigins"
                >CORS Allowed Origins</FieldLabel
              >
              <Input
                id="corsAllowedOrigins"
                v-model="detail.corsAllowedOrigins"
                placeholder="https://app.example.com, https://admin.example.com"
              />
              <p class="mt-1 text-xs text-muted-foreground">
                Comma-separated origins. Leave empty to block browser
                cross-origin requests for this key.
              </p>
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
              :disabled="issuing"
              class="w-full md:w-auto"
              @click.prevent="issue"
            >
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
      <CardHeader class="border-b bg-muted/40">
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
                Prefix: {{ key.keyPrefix }} | Active:
                {{ key.isActive ? "yes" : "no" }}
              </div>
              <div class="text-muted-foreground">
                Last used: {{ key.lastUsedAt || "never" }}
              </div>
              <div class="text-muted-foreground">
                CORS: {{ key.corsAllowedOrigins || "none" }}
              </div>
              <div class="text-muted-foreground">
                SQL: {{ key.sqlProvider || "Global" }} / connection:
                {{ key.hasSqlConnectionStringOverride ? "override" : "Global" }}
              </div>
            </div>
            <Button
              variant="destructive"
              :disabled="!key.isActive"
              @click="revoke(key.id)"
              >Revoke</Button
            >
          </div>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
