<script setup lang="ts">
import { onMounted, ref } from "vue";
import { toast } from "vue-sonner";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import {
  dryRunAuditRetention,
  executeAuditRetention,
  exportRuntimeAudit,
  getAuditRetentionPolicy,
  getRuntimeAudit,
} from "@/api/runtime";
import {
  buildAuditExecutionSummary,
  formatAuditDefinition,
} from "@/lib/auditPresentation";
import { parseAuditDrillDownQuery } from "@/lib/auditNavigation";

definePageMeta({
  layout: "default",
  permission: "/runtime/audit.view",
});

interface AuditItem {
  id: number;
  eventId: string;
  actorType: string;
  actorId?: string | null;
  action: string;
  target: string;
  detail?: string | null;
  result: string;
  requestId?: string | null;
  sessionId?: string | null;
  accessKeyId?: number | null;
  dbManagementId?: number | null;
  databaseName?: string | null;
  toolName?: string | null;
  operation?: string | null;
  durationMs?: number | null;
  returnedRows?: number | null;
  affectedRows?: number | null;
  approvalStatus?: string | null;
  errorCategory?: string | null;
  definition?: string | null;
  createdAt: string;
}

interface AuditRetentionPolicy {
  enabled: boolean;
  retentionDays: number;
  mode: string;
  runHourUtc: number;
}

const route = useRoute();
const page = ref(1);
const pageSize = ref(20);
const action = ref("");
const keyword = ref("");
const from = ref("");
const to = ref("");
const resultFilter = ref("");
const actor = ref("");
const dbManagementId = ref<number | undefined>();
const accessKeyId = ref<number | undefined>();
const toolName = ref("");
const totalCount = ref(0);
const items = ref<AuditItem[]>([]);
const selectedItem = ref<AuditItem | null>(null);
const loading = ref(false);
const retentionResult = ref<any>(null);
const retentionPolicy = ref<AuditRetentionPolicy | null>(null);
const retentionLoading = ref(false);
const retentionError = ref("");
const { $can } = useNuxtApp();

const currentFilters = () => ({
  action: action.value || undefined,
  keyword: keyword.value || undefined,
  from: from.value ? new Date(`${from.value}T00:00:00`).toISOString() : undefined,
  to: to.value ? new Date(`${to.value}T23:59:59.999`).toISOString() : undefined,
  result: resultFilter.value || undefined,
  actor: actor.value || undefined,
  dbManagementId: dbManagementId.value || undefined,
  accessKeyId: accessKeyId.value || undefined,
  toolName: toolName.value || undefined,
});

const applyRouteFilters = () => {
  const initial = parseAuditDrillDownQuery(route.query as Record<string, unknown>);
  from.value = initial.from || "";
  to.value = initial.to || "";
  dbManagementId.value = initial.dbManagementId;
  accessKeyId.value = initial.accessKeyId;
  toolName.value = initial.toolName || "";
};

const formatTime = (value?: string | null) => {
  if (!value) return "—";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
};

const resultClass = (result: string) =>
  result.toLowerCase() === "success"
    ? "border-emerald-300 bg-emerald-50 text-emerald-700 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-200"
    : "border-rose-300 bg-rose-50 text-rose-700 dark:border-rose-900 dark:bg-rose-950/30 dark:text-rose-200";

const executionSummary = (item: AuditItem) =>
  buildAuditExecutionSummary(item);

const openDetails = (item: AuditItem) => {
  selectedItem.value = item;
};

const copyValue = async (label: string, value?: string | number | null) => {
  if (value == null || value === "") return;
  try {
    await navigator.clipboard.writeText(String(value));
    toast.success(`${label} copied.`);
  } catch {
    toast.error(`Unable to copy ${label.toLowerCase()}.`);
  }
};

const load = async () => {
  loading.value = true;
  try {
    const result = await getRuntimeAudit(
      page.value,
      pageSize.value,
      currentFilters(),
    );
    items.value = result.items || [];
    totalCount.value = result.totalCount || 0;
  } finally {
    loading.value = false;
  }
};

const exportAudit = async (format: "csv" | "json") => {
  const blob = await exportRuntimeAudit(format, currentFilters());
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `audit-${new Date().toISOString()}.${format}`;
  link.click();
  URL.revokeObjectURL(url);
};

const loadRetentionPolicy = async () => {
  retentionLoading.value = true;
  retentionError.value = "";
  try {
    retentionPolicy.value = await getAuditRetentionPolicy();
  } catch {
    retentionError.value = "Unable to load the configured retention policy.";
  } finally {
    retentionLoading.value = false;
  }
};

const previewRetention = async () => {
  try {
    retentionResult.value = await dryRunAuditRetention();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to preview audit retention.");
  }
};

const runRetention = async () => {
  if (!confirm("Delete/archive all audit rows shown by the retention dry-run?")) return;
  try {
    retentionResult.value = await executeAuditRetention();
    await load();
  } catch (error: any) {
    toast.error(error?.response?.data || "Failed to run audit retention.");
  }
};

const nextPage = async () => {
  if (page.value * pageSize.value >= totalCount.value) return;
  page.value += 1;
  await load();
};

const prevPage = async () => {
  if (page.value <= 1) return;
  page.value -= 1;
  await load();
};

onMounted(async () => {
  applyRouteFilters();
  await Promise.all([
    load(),
    $can("/runtime/audit.edit") ? loadRetentionPolicy() : Promise.resolve(),
  ]);
});
</script>

<template>
  <div class="space-y-4">
    <Card>
      <CardHeader class="border-b">
        <CardTitle>Audit Logs</CardTitle>
        <CardDescription>
          Scan runtime and MCP activity quickly, then open an event for trace and execution details.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="mb-4 grid gap-2 md:grid-cols-3 xl:grid-cols-5">
          <Input v-model="action" placeholder="Filter action" />
          <Input v-model="keyword" placeholder="Keyword" />
          <Input v-model="resultFilter" placeholder="Result (success/failed)" />
          <Input v-model="actor" placeholder="Actor ID or type" />
          <Input v-model="toolName" placeholder="Tool name" />
          <Input v-model.number="accessKeyId" type="number" min="1" placeholder="Access Key ID" />
          <Input v-model.number="dbManagementId" type="number" min="1" placeholder="DB connection ID" />
          <Input v-model="from" type="date" aria-label="From date" />
          <Input v-model="to" type="date" aria-label="To date" />
          <Button @click="load">Search</Button>
          <Button v-if="$can('/runtime/audit.export')" variant="outline" @click="exportAudit('csv')">
            Export CSV
          </Button>
          <Button v-if="$can('/runtime/audit.export')" variant="outline" @click="exportAudit('json')">
            Export JSON
          </Button>
        </div>

        <div v-if="$can('/runtime/audit.edit')" class="mb-4 rounded border p-3 text-sm">
          <div class="font-medium">Retention policy</div>
          <div v-if="retentionLoading" class="mt-2 text-muted-foreground">
            Loading retention policy...
          </div>
          <div v-else-if="retentionError" class="mt-2 text-destructive">
            {{ retentionError }}
          </div>
          <div v-else-if="retentionPolicy && !retentionPolicy.enabled" class="mt-2 text-muted-foreground">
            Disabled. Set <code>Operability:AuditRetentionDays</code> to a positive value and restart the service to enable automatic and manual retention.
          </div>
          <div v-else-if="retentionPolicy" class="mt-2 flex flex-wrap items-center gap-2">
            <span class="text-muted-foreground">
              {{ retentionPolicy.mode }} records older than {{ retentionPolicy.retentionDays }} days · scheduled at {{ retentionPolicy.runHourUtc }}:00 UTC
            </span>
            <Button size="sm" variant="outline" @click="previewRetention">Dry run</Button>
            <Button size="sm" variant="destructive" :disabled="!retentionResult?.dryRun" @click="runRetention">
              Run configured retention
            </Button>
            <span v-if="retentionResult" class="text-muted-foreground">
              {{ retentionResult.matchingCount }} rows before {{ retentionResult.cutoff }} · {{ retentionResult.mode }}<template v-if="retentionResult.deletedCount"> · deleted {{ retentionResult.deletedCount }}</template>
            </span>
          </div>
        </div>

        <div v-if="loading" class="py-8 text-sm text-muted-foreground">
          Loading audit logs...
        </div>
        <div v-else-if="items.length === 0" class="rounded-lg border border-dashed py-12 text-center text-sm text-muted-foreground">
          No audit events match the current filters.
        </div>
        <div v-else class="divide-y rounded-lg border">
          <div
            v-for="item in items"
            :key="item.id"
            class="flex flex-col gap-3 p-4 transition hover:bg-muted/20 lg:flex-row lg:items-center lg:justify-between"
          >
            <div class="min-w-0 flex-1 space-y-2">
              <div class="flex flex-wrap items-center gap-2">
                <span class="font-medium">{{ item.action }}</span>
                <Badge variant="outline" :class="resultClass(item.result)">
                  {{ item.result }}
                </Badge>
                <Badge v-if="item.errorCategory" variant="outline" class="border-rose-300 text-rose-700">
                  {{ item.errorCategory }}
                </Badge>
                <span class="text-xs text-muted-foreground">{{ formatTime(item.createdAt) }}</span>
              </div>

              <div class="truncate text-sm text-muted-foreground">
                {{ item.target }} · {{ item.actorType }}<template v-if="item.actorId"> / {{ item.actorId }}</template>
              </div>

              <div class="flex flex-wrap gap-2 text-xs text-muted-foreground">
                <span v-if="item.toolName" class="rounded bg-muted px-2 py-1">Tool: {{ item.toolName }}</span>
                <span v-if="item.databaseName || item.dbManagementId" class="rounded bg-muted px-2 py-1">
                  DB: {{ item.databaseName || `#${item.dbManagementId}` }}
                </span>
                <span v-if="item.accessKeyId" class="rounded bg-muted px-2 py-1">Key #{{ item.accessKeyId }}</span>
                <span v-for="entry in executionSummary(item)" :key="entry" class="rounded bg-muted px-2 py-1">
                  {{ entry }}
                </span>
              </div>
            </div>

            <Button size="sm" variant="outline" @click="openDetails(item)">
              View details
            </Button>
          </div>
        </div>

        <div class="mt-4 flex flex-wrap items-center gap-2">
          <Button variant="outline" @click="prevPage" :disabled="page <= 1">Previous</Button>
          <span class="text-sm">Page {{ page }}</span>
          <Button variant="outline" @click="nextPage" :disabled="page * pageSize >= totalCount">Next</Button>
          <span class="text-sm text-muted-foreground">Total {{ totalCount }}</span>
        </div>
      </CardContent>
    </Card>

    <Sheet
      :open="selectedItem !== null"
      @update:open="(open) => { if (!open) selectedItem = null }"
    >
      <SheetContent side="right" class="w-full overflow-y-auto sm:max-w-xl">
        <template v-if="selectedItem">
          <SheetHeader class="pr-8">
            <div class="flex flex-wrap items-center gap-2">
              <SheetTitle>{{ selectedItem.action }}</SheetTitle>
              <Badge variant="outline" :class="resultClass(selectedItem.result)">
                {{ selectedItem.result }}
              </Badge>
            </div>
            <SheetDescription>
              {{ formatTime(selectedItem.createdAt) }} · {{ selectedItem.target }}
            </SheetDescription>
          </SheetHeader>

          <div class="space-y-5 px-4 pb-6">
            <section class="rounded-lg border p-4">
              <h3 class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                Identity
              </h3>
              <dl class="mt-3 grid gap-3 text-sm">
                <div>
                  <dt class="text-xs text-muted-foreground">Event ID</dt>
                  <dd class="mt-1 flex items-start gap-2">
                    <code class="min-w-0 flex-1 break-all text-xs">{{ selectedItem.eventId }}</code>
                    <Button size="sm" variant="outline" @click="copyValue('Event ID', selectedItem.eventId)">Copy</Button>
                  </dd>
                </div>
                <div class="grid gap-3 sm:grid-cols-2">
                  <div>
                    <dt class="text-xs text-muted-foreground">Actor</dt>
                    <dd>{{ selectedItem.actorType }}<template v-if="selectedItem.actorId"> / {{ selectedItem.actorId }}</template></dd>
                  </div>
                  <div>
                    <dt class="text-xs text-muted-foreground">Target</dt>
                    <dd class="break-all">{{ selectedItem.target }}</dd>
                  </div>
                </div>
              </dl>
            </section>

            <section class="rounded-lg border p-4">
              <h3 class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                Execution
              </h3>
              <dl class="mt-3 grid gap-3 text-sm sm:grid-cols-2">
                <div>
                  <dt class="text-xs text-muted-foreground">Tool</dt>
                  <dd>{{ selectedItem.toolName || "—" }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Operation</dt>
                  <dd>{{ selectedItem.operation || "—" }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Database</dt>
                  <dd>{{ selectedItem.databaseName || (selectedItem.dbManagementId ? `#${selectedItem.dbManagementId}` : "—") }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Access key</dt>
                  <dd>{{ selectedItem.accessKeyId ? `#${selectedItem.accessKeyId}` : "—" }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Duration</dt>
                  <dd>{{ selectedItem.durationMs != null ? `${selectedItem.durationMs} ms` : "—" }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Approval</dt>
                  <dd>{{ selectedItem.approvalStatus || "—" }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Returned rows</dt>
                  <dd>{{ selectedItem.returnedRows ?? "—" }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Affected rows</dt>
                  <dd>{{ selectedItem.affectedRows ?? "—" }}</dd>
                </div>
                <div v-if="selectedItem.errorCategory" class="sm:col-span-2">
                  <dt class="text-xs text-muted-foreground">Error category</dt>
                  <dd class="text-destructive">{{ selectedItem.errorCategory }}</dd>
                </div>
              </dl>
            </section>

            <section v-if="selectedItem.requestId || selectedItem.sessionId" class="rounded-lg border p-4">
              <h3 class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                Trace
              </h3>
              <div class="mt-3 space-y-3">
                <div v-if="selectedItem.requestId">
                  <div class="text-xs text-muted-foreground">Request ID</div>
                  <div class="mt-1 flex items-start gap-2">
                    <code class="min-w-0 flex-1 break-all text-xs">{{ selectedItem.requestId }}</code>
                    <Button size="sm" variant="outline" @click="copyValue('Request ID', selectedItem.requestId)">Copy</Button>
                  </div>
                </div>
                <div v-if="selectedItem.sessionId">
                  <div class="text-xs text-muted-foreground">Session ID</div>
                  <div class="mt-1 flex items-start gap-2">
                    <code class="min-w-0 flex-1 break-all text-xs">{{ selectedItem.sessionId }}</code>
                    <Button size="sm" variant="outline" @click="copyValue('Session ID', selectedItem.sessionId)">Copy</Button>
                  </div>
                </div>
              </div>
            </section>

            <section v-if="selectedItem.detail" class="rounded-lg border p-4">
              <h3 class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                Detail
              </h3>
              <p class="mt-3 whitespace-pre-wrap break-words text-sm">{{ selectedItem.detail }}</p>
            </section>

            <section v-if="selectedItem.definition" class="rounded-lg border p-4">
              <div class="flex items-center justify-between gap-2">
                <h3 class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                  Definition
                </h3>
                <Button size="sm" variant="outline" @click="copyValue('Definition', selectedItem.definition)">
                  Copy
                </Button>
              </div>
              <pre class="mt-3 max-h-80 overflow-auto whitespace-pre-wrap break-all rounded bg-muted/50 p-3 text-xs">{{ formatAuditDefinition(selectedItem.definition) }}</pre>
            </section>
          </div>
        </template>
      </SheetContent>
    </Sheet>
  </div>
</template>