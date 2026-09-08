<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { toast } from "vue-sonner";
import {
  getSecurityPolicy,
  updateSecurityPolicy,
  type SecurityPolicy,
} from "@/api/security";
import SqlExplainSimulator from "@/components/runtime/SqlExplainSimulator.vue";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import {
  buildSecurityPolicyPosture,
  securityPolicyFingerprint,
} from "@/lib/securityPolicyPresentation";

definePageMeta({
  layout: "default",
  permission: "/runtime/security.view",
});

const defaults: SecurityPolicy = {
  queryMaxRows: 1000,
  queryTimeoutSeconds: 30,
  requireWhereForUpdate: true,
  requireWhereForDelete: true,
  allowFullTableUpdate: false,
  allowFullTableDelete: false,
  dmlMaxAffectedRows: 100,
  keyPermitLimit: 120,
  keyWindowSeconds: 60,
  maxConcurrentSql: 16,
};

const policy = reactive<SecurityPolicy>({ ...defaults });
const loading = ref(false);
const loaded = ref(false);
const loadError = ref("");
const saving = ref(false);
const savedFingerprint = ref("");

const posture = computed(() => buildSecurityPolicyPosture(policy));
const hasUnsavedChanges = computed(
  () =>
    loaded.value &&
    savedFingerprint.value !== "" &&
    savedFingerprint.value !== securityPolicyFingerprint(policy),
);

const formatTime = (value?: string | null) => {
  if (!value) return "";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
};

const load = async () => {
  loading.value = true;
  loaded.value = false;
  loadError.value = "";
  savedFingerprint.value = "";

  try {
    Object.assign(policy, await getSecurityPolicy());
    savedFingerprint.value = securityPolicyFingerprint(policy);
    loaded.value = true;
  } catch (error: any) {
    loadError.value =
      "The effective server policy could not be loaded. Fallback values are not shown or editable.";
    toast.error(error?.response?.data?.error || "Failed to load security policy.");
  } finally {
    loading.value = false;
  }
};

const save = async () => {
  if (!loaded.value || !hasUnsavedChanges.value) return;

  saving.value = true;
  try {
    Object.assign(policy, await updateSecurityPolicy({ ...policy }));
    savedFingerprint.value = securityPolicyFingerprint(policy);
    toast.success("Security policy updated.");
  } catch (error: any) {
    toast.error(
      error?.response?.data?.error ||
        error?.response?.data ||
        "Failed to update security policy.",
    );
  } finally {
    saving.value = false;
  }
};

onMounted(load);
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-2xl font-semibold">Security Policy</h1>
      <p class="text-sm text-muted-foreground">
        Server-enforced limits apply to built-in and custom SQL tools.
      </p>
    </div>

    <div v-if="loading" class="text-sm text-muted-foreground">Loading policy...</div>

    <div
      v-else-if="!loaded"
      class="rounded-lg border border-destructive/40 bg-destructive/5 p-4"
    >
      <div class="font-medium text-destructive">Unable to load effective policy</div>
      <p class="mt-1 text-sm text-muted-foreground">{{ loadError }}</p>
      <Button class="mt-3" variant="outline" @click="load">Retry</Button>
    </div>

    <template v-else>
      <Card>
        <CardHeader class="border-b">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div>
              <CardTitle>Effective policy posture</CardTitle>
              <CardDescription>
                Read the mutation guardrails and runtime limits before changing individual values.
              </CardDescription>
            </div>
            <div class="flex flex-wrap items-center gap-2">
              <Badge
                variant="outline"
                :class="
                  posture.requiresReview
                    ? 'border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-200'
                    : 'border-emerald-300 bg-emerald-50 text-emerald-700 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-200'
                "
              >
                {{ posture.label }}
              </Badge>
              <Badge
                variant="outline"
                :class="
                  hasUnsavedChanges
                    ? 'border-amber-300 text-amber-800 dark:border-amber-900 dark:text-amber-200'
                    : ''
                "
              >
                {{ hasUnsavedChanges ? "Unsaved changes" : "Saved" }}
              </Badge>
            </div>
          </div>
        </CardHeader>
        <CardContent class="space-y-4 pt-4">
          <p class="text-sm text-muted-foreground">{{ posture.summary }}</p>

          <div class="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            <div
              v-for="fact in posture.facts"
              :key="fact.label"
              class="rounded-lg border p-3"
            >
              <div class="text-xs text-muted-foreground">{{ fact.label }}</div>
              <div class="mt-1 text-sm font-medium">{{ fact.value }}</div>
            </div>
          </div>

          <div
            v-if="posture.warnings.length"
            class="rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100"
          >
            <div class="font-medium">Mutation guardrails to review</div>
            <ul class="mt-2 list-disc space-y-1 pl-5">
              <li v-for="warning in posture.warnings" :key="warning">
                {{ warning }}
              </li>
            </ul>
          </div>

          <p
            v-if="policy.updatedAt || policy.updatedBy"
            class="text-xs text-muted-foreground"
          >
            Last saved<template v-if="policy.updatedAt"> {{ formatTime(policy.updatedAt) }}</template
            ><template v-if="policy.updatedBy"> by {{ policy.updatedBy }}</template>.
          </p>
        </CardContent>
      </Card>

      <SqlExplainSimulator />

      <Card>
        <CardHeader>
          <CardTitle>Query limits</CardTitle>
          <CardDescription>Bound the cost of each SELECT operation.</CardDescription>
        </CardHeader>
        <CardContent class="grid gap-4 md:grid-cols-2">
          <Field>
            <FieldLabel for="queryMaxRows">Maximum returned rows</FieldLabel>
            <Input
              id="queryMaxRows"
              v-model.number="policy.queryMaxRows"
              type="number"
              min="1"
              max="100000"
            />
            <FieldDescription>The server clamps larger or missing LIMIT values.</FieldDescription>
          </Field>
          <Field>
            <FieldLabel for="queryTimeoutSeconds">SQL timeout (seconds)</FieldLabel>
            <Input
              id="queryTimeoutSeconds"
              v-model.number="policy.queryTimeoutSeconds"
              type="number"
              min="1"
              max="600"
            />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>DML guardrails</CardTitle>
          <CardDescription>
            These checks run before Elicitation and cannot be overridden by approval.
          </CardDescription>
        </CardHeader>
        <CardContent class="grid gap-5 md:grid-cols-2">
          <Field>
            <FieldLabel for="dmlMaxAffectedRows">Maximum affected rows</FieldLabel>
            <Input
              id="dmlMaxAffectedRows"
              v-model.number="policy.dmlMaxAffectedRows"
              type="number"
              min="1"
              max="1000000"
            />
          </Field>
          <div />
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Require WHERE for UPDATE</FieldLabel>
              <FieldDescription>Reject UPDATE statements without conditions.</FieldDescription>
            </div>
            <Switch v-model="policy.requireWhereForUpdate" />
          </Field>
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Allow full-table UPDATE</FieldLabel>
              <FieldDescription>Both this and the WHERE requirement must permit it.</FieldDescription>
            </div>
            <Switch v-model="policy.allowFullTableUpdate" />
          </Field>
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Require WHERE for DELETE</FieldLabel>
              <FieldDescription>Reject DELETE statements without conditions.</FieldDescription>
            </div>
            <Switch v-model="policy.requireWhereForDelete" />
          </Field>
          <Field orientation="horizontal">
            <div>
              <FieldLabel>Allow full-table DELETE</FieldLabel>
              <FieldDescription>Both this and the WHERE requirement must permit it.</FieldDescription>
            </div>
            <Switch v-model="policy.allowFullTableDelete" />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Resource limits</CardTitle>
          <CardDescription>
            Key and concurrency limits protect authenticated SQL workloads.
            Pre-auth IP limits are configured at server startup.
          </CardDescription>
        </CardHeader>
        <CardContent class="grid gap-4 md:grid-cols-2">
          <Field>
            <FieldLabel>Key requests per window</FieldLabel>
            <Input v-model.number="policy.keyPermitLimit" type="number" min="1" />
          </Field>
          <Field>
            <FieldLabel>Key window (seconds)</FieldLabel>
            <Input v-model.number="policy.keyWindowSeconds" type="number" min="1" />
          </Field>
          <Field>
            <FieldLabel>Maximum concurrent SQL operations</FieldLabel>
            <Input v-model.number="policy.maxConcurrentSql" type="number" min="1" />
          </Field>
        </CardContent>
      </Card>

      <div class="flex justify-end">
        <Button
          v-permission="'edit'"
          :disabled="saving || !hasUnsavedChanges"
          @click="save"
        >
          {{ saving ? "Saving..." : hasUnsavedChanges ? "Save policy" : "Saved" }}
        </Button>
      </div>
    </template>
  </div>
</template>
