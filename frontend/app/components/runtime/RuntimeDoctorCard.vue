<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { toast } from "vue-sonner";
import { getRuntimeDoctor, type RuntimeDoctorResponse } from "@/api/doctor";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  doctorStatusClass,
  doctorSummary,
  groupDoctorChecks,
} from "@/lib/runtimeDoctorPresentation";

const loading = ref(false);
const result = ref<RuntimeDoctorResponse | null>(null);
const summary = computed(() => (result.value ? doctorSummary(result.value) : null));
const groups = computed(() =>
  result.value ? groupDoctorChecks(result.value.checks) : {},
);

const load = async () => {
  loading.value = true;
  try {
    result.value = await getRuntimeDoctor();
  } catch (error: any) {
    toast.error(error?.response?.data?.error || "Failed to run configuration doctor.");
  } finally {
    loading.value = false;
  }
};

onMounted(load);
</script>

<template>
  <Card>
    <CardHeader class="border-b">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <CardTitle>Configuration Doctor</CardTitle>
          <CardDescription>
            Deployment and runtime-readiness checks derived from the effective server configuration.
            Secret values are never returned to this page.
          </CardDescription>
        </div>
        <div class="flex items-center gap-2">
          <Badge
            v-if="result"
            variant="outline"
            :class="doctorStatusClass(result.overallStatus)"
          >
            {{ result.overallStatus }}
          </Badge>
          <Button size="sm" variant="outline" :disabled="loading" @click="load">
            {{ loading ? "Checking..." : "Run doctor" }}
          </Button>
        </div>
      </div>
    </CardHeader>

    <CardContent class="space-y-4 pt-4">
      <div v-if="loading && !result" class="text-sm text-muted-foreground">
        Inspecting effective configuration...
      </div>

      <template v-if="result">
        <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Environment</div>
            <div class="mt-1 text-sm font-medium">{{ result.environment }}</div>
          </div>
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Deployment mode</div>
            <div class="mt-1 text-sm font-medium">{{ result.deploymentMode }}</div>
          </div>
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Errors</div>
            <div class="mt-1 text-sm font-medium">{{ summary?.errors ?? 0 }}</div>
          </div>
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Warnings</div>
            <div class="mt-1 text-sm font-medium">{{ summary?.warnings ?? 0 }}</div>
          </div>
          <div class="rounded-lg border p-3">
            <div class="text-xs text-muted-foreground">Healthy checks</div>
            <div class="mt-1 text-sm font-medium">{{ summary?.healthy ?? 0 }}</div>
          </div>
        </div>

        <div
          v-if="result.overallStatus !== 'Healthy'"
          class="rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100"
        >
          Fix <strong>Error</strong> items before treating the deployment as ready. Warnings indicate
          a configuration that can work but deserves operator review, especially for multi-node or
          internet-facing deployments.
        </div>

        <div class="grid gap-4 xl:grid-cols-2">
          <div
            v-for="(checks, category) in groups"
            :key="category"
            class="rounded-lg border"
          >
            <div class="border-b px-4 py-3 text-sm font-semibold">{{ category }}</div>
            <div class="divide-y">
              <div v-for="check in checks" :key="check.id" class="space-y-2 p-4 text-sm">
                <div class="flex flex-wrap items-center justify-between gap-2">
                  <div class="font-medium">{{ check.title }}</div>
                  <Badge variant="outline" :class="doctorStatusClass(check.status)">
                    {{ check.status }}
                  </Badge>
                </div>
                <p class="text-muted-foreground">{{ check.detail }}</p>
                <p v-if="check.action" class="text-xs">
                  <span class="font-medium">Action:</span> {{ check.action }}
                </p>
                <div class="text-[11px] text-muted-foreground">{{ check.id }}</div>
              </div>
            </div>
          </div>
        </div>

        <p class="text-xs text-muted-foreground">
          Last checked {{ new Date(result.generatedAtUtc).toLocaleString() }}. The doctor validates
          configuration posture and onboarding state; provider-specific database health remains in
          the scheduled Database health section below.
        </p>
      </template>
    </CardContent>
  </Card>
</template>
