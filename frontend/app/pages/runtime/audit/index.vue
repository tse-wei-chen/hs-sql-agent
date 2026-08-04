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
import { Input } from "@/components/ui/input";
import { dryRunAuditRetention, executeAuditRetention, exportRuntimeAudit, getRuntimeAudit } from "@/api/runtime";

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
const loading = ref(false);
const retentionResult = ref<any>(null);
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
  const link = document.createElement("a"); link.href = url; link.download = `audit-${new Date().toISOString()}.${format}`; link.click();
  URL.revokeObjectURL(url);
};
const previewRetention = async () => { retentionResult.value = await dryRunAuditRetention(); };
const runRetention = async () => {
  if (!confirm("Delete/archive all audit rows shown by the retention dry-run?")) return;
  retentionResult.value = await executeAuditRetention(); await load();
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

onMounted(load);
</script>

<template>
  <div class="space-y-4">
    <Card>
      <CardHeader class="border-b ">
        <CardTitle>Audit Logs</CardTitle>
        <CardDescription>Runtime settings and MCP key operation history.</CardDescription>
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
          <Button v-if="$can('/runtime/audit.export')" variant="outline" @click="exportAudit('csv')">Export CSV</Button>
          <Button v-if="$can('/runtime/audit.export')" variant="outline" @click="exportAudit('json')">Export JSON</Button>
        </div>

        <div v-if="$can('/runtime/audit.edit')" class="mb-4 rounded border p-3 text-sm">
          <div class="font-medium">Retention policy</div>
          <div class="mt-2 flex flex-wrap items-center gap-2">
            <Button size="sm" variant="outline" @click="previewRetention">Dry run</Button>
            <Button size="sm" variant="destructive" :disabled="!retentionResult?.dryRun" @click="runRetention">Run configured retention</Button>
            <span v-if="retentionResult" class="text-muted-foreground">{{ retentionResult.matchingCount }} rows before {{ retentionResult.cutoff }} · {{ retentionResult.mode }}<template v-if="retentionResult.deletedCount"> · deleted {{ retentionResult.deletedCount }}</template></span>
          </div>
        </div>

        <div v-if="loading">Loading audit logs...</div>
        <div v-else class="space-y-2">
          <div v-for="item in items" :key="item.id" class="rounded border bg-card p-3 text-sm">
            <div class="font-medium">{{ item.action }} <span class="rounded-full px-2 py-0.5 text-xs" :class="item.result.toLowerCase() === 'success'
              ? 'bg-emerald-100 text-emerald-700'
              : 'bg-rose-100 text-rose-700'
              ">
                {{ item.result }}
              </span>
            </div>
            <div class="text-muted-foreground">
              target: {{ item.target }} | actor: {{ item.actorType }}
              {{ item.actorId || "" }}
            </div>
            <div class="text-muted-foreground">{{ item.createdAt }}</div>
            <div v-if="item.toolName || item.databaseName" class="text-muted-foreground">
              tool: {{ item.toolName || "—" }} | DB: {{ item.databaseName || item.dbManagementId || "—" }}
              | key: {{ item.accessKeyId || "—" }}
            </div>
            <div v-if="item.durationMs != null || item.returnedRows != null || item.affectedRows != null" class="text-muted-foreground">
              duration: {{ item.durationMs ?? "—" }} ms | returned: {{ item.returnedRows ?? "—" }}
              | affected: {{ item.affectedRows ?? "—" }} | approval: {{ item.approvalStatus || "—" }}
            </div>
            <div v-if="item.requestId" class="text-xs text-muted-foreground">
              request: {{ item.requestId }} <span v-if="item.sessionId">| session: {{ item.sessionId }}</span>
            </div>
            <div v-if="item.detail" class="mt-1">{{ item.detail }}</div>
            <div v-if="item.definition" class="mt-1 break-all font-mono text-xs">{{ item.definition }}</div>
          </div>
        </div>

        <div class="mt-4 flex items-center gap-2">
          <Button variant="outline" @click="prevPage" :disabled="page <= 1">Previous</Button>
          <span class="text-sm">Page {{ page }}</span>
          <Button variant="outline" @click="nextPage" :disabled="page * pageSize >= totalCount">Next</Button>
          <span class="text-sm text-muted-foreground">Total {{ totalCount }}</span>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
