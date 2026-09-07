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
import { Input } from "@/components/ui/input";
import ComboboxInput from "@/components/ComboboxInput.vue";
import {
  getDbHealth,
  getDeliveryStatuses,
  getKeyUsage,
  getOperabilityMetrics,
  retryDelivery,
} from "@/api/runtime";
import {
  buildDatabaseFilterOptions,
  buildKeyFilterOptions,
  resolveOperabilityFilterId,
} from "@/lib/operabilityFilters";

definePageMeta({
  layout: "default",
  permission: "/runtime/operability.view",
});

const { $can } = useNuxtApp();
const from = ref("");
const to = ref("");
const loading = ref(false);
const selectedDatabase = ref("All databases");
const selectedKey = ref("All keys");
const toolName = ref("");
const metrics = ref<any>({});
const health = ref<any[]>([]);
const keyUsage = ref<any[]>([]);
const keyFilterSource = ref<any[]>([]);
const deliveries = ref<any[]>([]);

const databaseFilterOptions = computed(() =>
  buildDatabaseFilterOptions(health.value),
);
const keyFilterOptions = computed(() => buildKeyFilterOptions(keyFilterSource.value));
const databaseFilterLabels = computed(() =>
  databaseFilterOptions.value.map((option) => option.label),
);
const keyFilterLabels = computed(() =>
  keyFilterOptions.value.map((option) => option.label),
);

const filters = () => ({
  from: from.value
    ? new Date(`${from.value}T00:00:00`).toISOString()
    : undefined,
  to: to.value
    ? new Date(`${to.value}T23:59:59.999`).toISOString()
    : undefined,
  dbManagementId: resolveOperabilityFilterId(
    selectedDatabase.value,
    databaseFilterOptions.value,
  ),
  accessKeyId: resolveOperabilityFilterId(
    selectedKey.value,
    keyFilterOptions.value,
  ),
  toolName: toolName.value || undefined,
});

const load = async () => {
  loading.value = true;
  try {
    const activeFilters = filters();
    const [metricsResult, healthResult, keyUsageResult, deliveriesResult] =
      await Promise.all([
        getOperabilityMetrics(activeFilters),
        getDbHealth(),
        getKeyUsage(activeFilters),
        getDeliveryStatuses(),
      ]);

    metrics.value = metricsResult;
    health.value = healthResult;
    keyUsage.value = keyUsageResult;
    deliveries.value = deliveriesResult;

    const isUnfilteredKeyUsage =
      !activeFilters.from &&
      !activeFilters.to &&
      !activeFilters.dbManagementId &&
      !activeFilters.accessKeyId &&
      !activeFilters.toolName;
    if (isUnfilteredKeyUsage || keyFilterSource.value.length === 0) {
      keyFilterSource.value = keyUsageResult;
    }

    if (!databaseFilterLabels.value.includes(selectedDatabase.value)) {
      selectedDatabase.value = "All databases";
    }
    if (!keyFilterLabels.value.includes(selectedKey.value)) {
      selectedKey.value = "All keys";
    }
  } finally {
    loading.value = false;
  }
};

const retry = async (id: number) => {
  await retryDelivery(id);
  await load();
};

const percent = (value?: number) => `${((value || 0) * 100).toFixed(1)}%`;

onMounted(load);
</script>

<template>
  <div class="space-y-4">
    <Card>
      <CardHeader class="border-b">
        <CardTitle>Operability</CardTitle>
        <CardDescription>
          Scheduled database health, SQL execution metrics, key usage, and webhook delivery state.
        </CardDescription>
      </CardHeader>
      <CardContent class="pt-4">
        <div class="flex flex-wrap items-end gap-2">
          <div>
            <div class="mb-1 text-xs text-muted-foreground">From</div>
            <Input v-model="from" type="date" />
          </div>
          <div>
            <div class="mb-1 text-xs text-muted-foreground">To</div>
            <Input v-model="to" type="date" />
          </div>
          <div>
            <div class="mb-1 text-xs text-muted-foreground">Database</div>
            <ComboboxInput
              v-model="selectedDatabase"
              :options="databaseFilterLabels"
              :allow-custom="false"
              search-placeholder="Search databases..."
              empty-text="No databases found."
              class="w-64"
            />
          </div>
          <div>
            <div class="mb-1 text-xs text-muted-foreground">MCP Key</div>
            <ComboboxInput
              v-model="selectedKey"
              :options="keyFilterLabels"
              :allow-custom="false"
              search-placeholder="Search keys..."
              empty-text="No keys with recorded usage found."
              class="w-64"
            />
          </div>
          <div>
            <div class="mb-1 text-xs text-muted-foreground">Tool</div>
            <Input v-model="toolName" placeholder="All" />
          </div>
          <Button :disabled="loading" @click="load">
            {{ loading ? "Loading..." : "Refresh" }}
          </Button>
        </div>
        <p class="mt-2 text-xs text-muted-foreground">
          Database and key selectors use Operability data only; no additional management permission is required.
        </p>
      </CardContent>
    </Card>

    <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
      <Card>
        <CardHeader>
          <CardDescription>Query / DML</CardDescription>
          <CardTitle>{{ metrics.queryCount || 0 }} / {{ metrics.dmlCount || 0 }}</CardTitle>
        </CardHeader>
      </Card>
      <Card>
        <CardHeader>
          <CardDescription>Success rate</CardDescription>
          <CardTitle>{{ percent(metrics.successRate) }}</CardTitle>
        </CardHeader>
      </Card>
      <Card>
        <CardHeader>
          <CardDescription>
            p50 / p95 latency<template v-if="metrics.latencySampled">
              (latest {{ metrics.latencySampleSize }} samples)</template
            >
          </CardDescription>
          <CardTitle>
            {{ metrics.p50LatencyMs ?? "—" }} / {{ metrics.p95LatencyMs ?? "—" }} ms
          </CardTitle>
        </CardHeader>
      </Card>
      <Card>
        <CardHeader>
          <CardDescription>Slow / IP 429 / Key 429</CardDescription>
          <CardTitle>
            {{ metrics.slowQueryCount || 0 }} / {{ metrics.ipRateLimitCount || 0 }} /
            {{ metrics.keyRateLimitCount || 0 }}
          </CardTitle>
        </CardHeader>
      </Card>
    </div>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Database health</CardTitle>
        <CardDescription>
          These results come from scheduled probes, not manual Test Connection.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-2 pt-4">
        <div
          v-for="item in health"
          :key="item.dbManagementId"
          class="rounded border p-3 text-sm"
        >
          <div class="font-medium">
            {{ item.name }} ({{ item.provider }}) — {{ item.status }}
          </div>
          <div class="text-muted-foreground">
            latency {{ item.latencyMs ?? "—" }} ms · failures
            {{ item.consecutiveFailures }} · last success
            {{ item.lastSuccessAt || "never" }}<template v-if="item.outageStartedAt">
              · outage since {{ item.outageStartedAt }}</template
            >
          </div>
          <div v-if="item.lastError" class="text-destructive">
            {{ item.lastError }}
          </div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Key usage</CardTitle>
        <CardDescription>Usage and quota-hit rate for the selected time range.</CardDescription>
      </CardHeader>
      <CardContent class="space-y-2 pt-4">
        <div
          v-for="item in keyUsage"
          :key="item.accessKeyId"
          class="rounded border p-3 text-sm"
        >
          <div class="font-medium">{{ item.name }} (#{{ item.accessKeyId }})</div>
          <div class="text-muted-foreground">
            audited tool operations {{ item.requestCount }} · success {{ item.successCount }} ·
            failed {{ item.failureCount }} · HTTP rate-limit 429 {{ item.rateLimitCount }}
            ({{ percent(item.rateLimitRejectionRate) }}) · last activity
            {{ item.lastUsedAt || "never" }}
          </div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="border-b">
        <CardTitle>Webhook deliveries</CardTitle>
        <CardDescription>
          Alert and SIEM outbox status, including retries and dead letters.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-2 pt-4">
        <div
          v-for="item in deliveries"
          :key="item.id"
          class="flex items-center justify-between gap-3 rounded border p-3 text-sm"
        >
          <div>
            <div class="font-medium">
              {{ item.category }} #{{ item.id }} — {{ item.status }}
            </div>
            <div class="text-muted-foreground">
              attempts {{ item.attemptCount }} ·
              {{ item.lastError || item.deliveredAt || item.createdAt }}
            </div>
          </div>
          <Button
            v-if="$can('/runtime/operability.edit') && item.status === 'dead-letter'"
            size="sm"
            variant="outline"
            @click="retry(item.id)"
          >
            Retry
          </Button>
        </div>
      </CardContent>
    </Card>
  </div>
</template>
